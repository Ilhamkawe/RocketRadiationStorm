extern alias SteamworksNET;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Timers;
using Rocket.API.Collections;
using Rocket.Core.Plugins;
using Rocket.Core.Utils;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;
using UnityEngine;
using CSteamID = SteamworksNET::Steamworks.CSteamID;
using RocketLogger = Rocket.Core.Logging.Logger;

namespace RocketRadiationStorm
{
    public class RadiationStormPlugin : RocketPlugin<RadiationStormPluginConfiguration>
    {
        private Timer _tickTimer;
        private Timer _autoStormTimer;
        private Timer _stormDurationTimer;
        private Timer _damageDelayTimer;
        private readonly HashSet<CSteamID> _effectRecipients = new HashSet<CSteamID>();
        private bool _stormActive;
        private bool _damageActive;
        private DateTime? _nextStormTimeUtc;
        private readonly System.Random _random = new System.Random();
        private MethodInfo _weatherCommandMethod;
        private static readonly PropertyInfo OxygenVolumeManagerInstanceProperty = typeof(OxygenVolumeManager).GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo OxygenVolumeManagerInstanceField = typeof(OxygenVolumeManager).GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo OxygenVolumeManagerBreathableMethod = typeof(OxygenVolumeManager).GetMethod("IsPositionInsideBreathableVolume", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private bool _oxygenReflectionWarningLogged;
        private readonly List<SafezoneRadiatorInfo> _safezoneRadiators = new List<SafezoneRadiatorInfo>();
        private DateTime _safezoneRadiatorsCacheExpiryUtc = DateTime.MinValue;
        private readonly Dictionary<Type, SafezoneReflectionCache> _safezoneReflectionCache = new Dictionary<Type, SafezoneReflectionCache>();
        private bool _safezoneReflectionWarningLogged;
        private static readonly BindingFlags DeadzoneReflectionFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private DeadzoneNode _createdDeadzoneNode;
        private List<DeadzoneNode> _existingDeadzoneNodes;
        private Dictionary<DeadzoneNode, float> _originalDeadzoneRadii;
        private static readonly FieldInfo LevelNodesDeadzoneNodesField = FindDeadzoneNodesField();
        
        private static FieldInfo FindDeadzoneNodesField()
        {
            var type = typeof(LevelNodes);
            var possibleNames = new[] { "deadzoneNodes", "deadzones", "_deadzoneNodes", "m_DeadzoneNodes", "DeadzoneNodes" };
            
            foreach (var name in possibleNames)
            {
                var field = type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    return field;
                }
            }
            
            return null;
        }
        private static readonly MethodInfo LevelNodesRegisterDeadzoneMethod = typeof(LevelNodes).GetMethod("registerDeadzone", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo LevelNodesRemoveDeadzoneMethod = typeof(LevelNodes).GetMethod("removeDeadzone", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo LevelSizeField = typeof(Level).GetField("size", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly PropertyInfo LevelSizeProperty = typeof(Level).GetProperty("size", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo LevelNodesLoadDeadzonesMethod = typeof(LevelNodes).GetMethod("loadDeadzones", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo LevelNodesRefreshDeadzonesMethod = typeof(LevelNodes).GetMethod("refreshDeadzones", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        public static RadiationStormPlugin Instance { get; private set; }

        protected override void Load()
        {
            Instance = this;
            _stormActive = false;
            _damageActive = false;

            InitializeTickTimer();
            InitializeAutoStormScheduler();
            
            // Subscribe to player connected event to apply effect to new players
            Provider.onServerConnected += OnPlayerConnected;

            RocketLogger.Log("[RadiationStorm] Plugin loaded.");
        }

        protected override void Unload()
        {
            StopTickTimer();
            StopAutoStormTimer();
            StopStormDurationTimer();
            StopDamageDelayTimer();
            ClearRadiationEffectAll();
            RemoveDeadzone();
            
            // Unsubscribe from player connected event
            Provider.onServerConnected -= OnPlayerConnected;
            
            Instance = null;
            RocketLogger.Log("[RadiationStorm] Plugin unloaded.");
        }

        public override TranslationList DefaultTranslations => new TranslationList
        {
            { "storm_start", "Radiation storm has begun!" },
            { "storm_stop", "Radiation storm has ended." },
            { "storm_already_active", "Radiation storm is already active." },
            { "storm_not_active", "No active radiation storm." },
            { "storm_status", "Radiation storm is currently {0}." },
            { "storm_next", "Next radiation storm in {0}." }
        };

        private void InitializeTickTimer()
        {
            StopTickTimer();

            var interval = Math.Max(0.5, Configuration.Instance.TickIntervalSeconds);
            _tickTimer = new Timer(interval * 1000);
            _tickTimer.AutoReset = true;
            _tickTimer.Elapsed += OnTimerElapsed;
            _tickTimer.Start();
        }

        private void StopTickTimer()
        {
            if (_tickTimer == null)
            {
                return;
            }

            _tickTimer.Elapsed -= OnTimerElapsed;
            _tickTimer.Stop();
            _tickTimer.Dispose();
            _tickTimer = null;
        }

        private void OnTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (!_stormActive || !_damageActive)
            {
                return;
            }

            TaskDispatcher.QueueOnMainThread(ApplyRadiationTick);
        }

        private void ApplyRadiationTick()
        {
            // Method ini untuk apply/maintain effect dan damage ke semua player secara berkala.
            
            var applyEffect = Configuration.Instance.UseRadiationEffect && Configuration.Instance.RadiationEffectId != 0;
            var applyDamage = Configuration.Instance.InfectionDamagePerTick > 0;

            if (!applyEffect && !applyDamage)
            {
                return;
            }

            foreach (var steamPlayer in Provider.clients)
            {
                var player = UnturnedPlayer.FromSteamPlayer(steamPlayer);

                if (player == null || player.Dead)
                {
                    continue;
                }

                if (!Configuration.Instance.TargetUnturnedAdmins && player.IsAdmin)
                {
                    continue;
                }

                if (IsPlayerProtectedFromRadiation(steamPlayer))
                {
                    continue;
                }

                // Apply effect jika enabled
                if (applyEffect)
                {
                    EnsureRadiationEffect(player, steamPlayer);
                }

                // Apply damage jika enabled
                if (applyDamage)
                {
                    ApplyRadiationDamage(player);
                }
            }
        }

        public void StartStorm()
        {
            if (_stormActive)
            {
                throw new InvalidOperationException(Translate("storm_already_active"));
            }

            _stormActive = true;
            StopAutoStormTimerInternal();
            _nextStormTimeUtc = null;
            StartStormDurationCountdown();
            ActivateWeather();
            CreateDeadzone();
            ScheduleDamageActivation();
            Broadcast("storm_start");
        }

        public void StopStorm()
        {
            if (!_stormActive)
            {
                throw new InvalidOperationException(Translate("storm_not_active"));
            }

            _stormActive = false;
            DeactivateDamagePhase();
            StopStormDurationTimer();
            DeactivateWeather();
            RemoveDeadzone();
            Broadcast("storm_stop");
            ScheduleNextAutoStorm();
        }

        public bool StormActive => _stormActive;
        public DateTime? NextStormTimeUtc => _nextStormTimeUtc;
        public bool AutoStormEnabled => Configuration.Instance.AutoStormEnabled;

        private void Broadcast(string translationKey)
        {
            if (!Configuration.Instance.BroadcastMessages)
            {
                return;
            }

            UnturnedChat.Say(Translate(translationKey), Color.green);
        }

        private void InitializeAutoStormScheduler()
        {
            if (!Configuration.Instance.AutoStormEnabled)
            {
                StopAutoStormTimer();
                return;
            }

            if (_autoStormTimer == null)
            {
                _autoStormTimer = new Timer
                {
                    AutoReset = false
                };
                _autoStormTimer.Elapsed += OnAutoStormTimerElapsed;
            }

            ScheduleNextAutoStorm();
        }

        private void ScheduleNextAutoStorm()
        {
            if (_autoStormTimer == null || !Configuration.Instance.AutoStormEnabled)
            {
                _nextStormTimeUtc = null;
                return;
            }

            var min = Math.Max(0.1, Configuration.Instance.AutoStormMinIntervalMinutes);
            var max = Math.Max(min, Configuration.Instance.AutoStormMaxIntervalMinutes);
            var minutes = min.Equals(max) ? min : min + _random.NextDouble() * (max - min);
            var interval = TimeSpan.FromMinutes(minutes);

            _nextStormTimeUtc = DateTime.UtcNow.Add(interval);
            _autoStormTimer.Interval = Math.Max(1000, interval.TotalMilliseconds);
            _autoStormTimer.Start();
        }

        private void OnAutoStormTimerElapsed(object sender, ElapsedEventArgs e)
        {
            TaskDispatcher.QueueOnMainThread(() =>
            {
                if (_stormActive)
                {
                    ScheduleNextAutoStorm();
                    return;
                }

                try
                {
                    StartStorm();
                }
                catch (Exception ex)
                {
                    RocketLogger.LogWarning($"[RadiationStorm] Failed to start auto storm: {ex.Message}");
                }
            });
        }

        private void StartStormDurationCountdown()
        {
            StopStormDurationTimer();

            var durationSeconds = Math.Max(0, Configuration.Instance.StormDurationSeconds);
            if (durationSeconds <= 0)
            {
                return;
            }

            _stormDurationTimer = new Timer(durationSeconds * 1000)
            {
                AutoReset = false
            };
            _stormDurationTimer.Elapsed += OnStormDurationElapsed;
            _stormDurationTimer.Start();
        }

        private void OnStormDurationElapsed(object sender, ElapsedEventArgs e)
        {
            TaskDispatcher.QueueOnMainThread(() =>
            {
                if (!_stormActive)
                {
                    return;
                }

                try
                {
                    StopStorm();
                }
                catch (InvalidOperationException)
                {
                    // ignored
                }
            });
        }

        private void ActivateWeather()
        {
            if (!Configuration.Instance.UseWeather || string.IsNullOrEmpty(Configuration.Instance.WeatherGuid))
            {
                return;
            }

            try
            {
                ExecuteWeatherCommand($"weather {Configuration.Instance.WeatherGuid}");
                RocketLogger.Log($"[RadiationStorm] Activated weather: {Configuration.Instance.WeatherGuid}");
            }
            catch (Exception ex)
            {
                RocketLogger.LogWarning($"[RadiationStorm] Unable to start weather: {ex.Message}");
            }
        }

        private void DeactivateWeather()
        {
            if (!Configuration.Instance.UseWeather || string.IsNullOrEmpty(Configuration.Instance.WeatherGuid))
            {
                return;
            }

            try
            {
                ExecuteWeatherCommand("weather none");
                RocketLogger.Log("[RadiationStorm] Deactivated weather.");
            }
            catch (Exception ex)
            {
                RocketLogger.LogWarning($"[RadiationStorm] Unable to stop weather: {ex.Message}");
            }
        }

        private void StopAutoStormTimerInternal()
        {
            if (_autoStormTimer == null)
            {
                return;
            }

            _autoStormTimer.Stop();
        }

        private void StopAutoStormTimer()
        {
            if (_autoStormTimer == null)
            {
                return;
            }

            _autoStormTimer.Elapsed -= OnAutoStormTimerElapsed;
            _autoStormTimer.Stop();
            _autoStormTimer.Dispose();
            _autoStormTimer = null;
        }

        private void StopStormDurationTimer()
        {
            if (_stormDurationTimer == null)
            {
                return;
            }

            _stormDurationTimer.Elapsed -= OnStormDurationElapsed;
            _stormDurationTimer.Stop();
            _stormDurationTimer.Dispose();
            _stormDurationTimer = null;
        }

        private void ScheduleDamageActivation()
        {
            _damageActive = false;
            StopDamageDelayTimer();

            var delaySeconds = Math.Max(0, Configuration.Instance.WeatherDamageDelaySeconds);
            if (delaySeconds <= 0)
            {
                ActivateDamagePhase();
                return;
            }

            _damageDelayTimer = new Timer(delaySeconds * 1000)
            {
                AutoReset = false
            };
            _damageDelayTimer.Elapsed += OnDamageDelayElapsed;
            _damageDelayTimer.Start();
        }

        private void OnDamageDelayElapsed(object sender, ElapsedEventArgs e)
        {
            TaskDispatcher.QueueOnMainThread(ActivateDamagePhase);
        }

        private void ActivateDamagePhase()
        {
            StopDamageDelayTimer();

            if (_damageActive)
            {
                return;
            }

            _damageActive = true;

            RocketLogger.Log("[RadiationStorm] Damage phase activated.");
            
            if (Configuration.Instance.UseRadiationEffect)
            {
                ApplyRadiationEffectAll();
                RocketLogger.Log($"[RadiationStorm] Radiation effect enabled (ID: {Configuration.Instance.RadiationEffectId}).");
            }
            else
            {
                RocketLogger.Log("[RadiationStorm] Radiation effect disabled.");
            }

            if (Configuration.Instance.InfectionDamagePerTick > 0)
            {
                RocketLogger.Log($"[RadiationStorm] Manual damage enabled: {Configuration.Instance.InfectionDamagePerTick} damage per tick (every {Configuration.Instance.TickIntervalSeconds}s).");
            }
            else
            {
                RocketLogger.LogWarning("[RadiationStorm] ⚠ Manual damage is DISABLED (InfectionDamagePerTick = 0).");
                RocketLogger.LogWarning("[RadiationStorm] ⚠ No damage will be dealt to players unless deadzone node is created.");
            }
        }

        private void DeactivateDamagePhase()
        {
            _damageActive = false;
            StopDamageDelayTimer();
            ClearRadiationEffectAll();
        }

        private void StopDamageDelayTimer()
        {
            if (_damageDelayTimer == null)
            {
                return;
            }

            _damageDelayTimer.Elapsed -= OnDamageDelayElapsed;
            _damageDelayTimer.Stop();
            _damageDelayTimer.Dispose();
            _damageDelayTimer = null;
        }

        private void ApplyRadiationEffectAll()
        {
            if (!Configuration.Instance.UseRadiationEffect || Configuration.Instance.RadiationEffectId == 0)
            {
                return;
            }

            foreach (var steamPlayer in Provider.clients)
            {
                var player = UnturnedPlayer.FromSteamPlayer(steamPlayer);
                if (player == null || player.Dead)
                {
                    continue;
                }

                if (!Configuration.Instance.TargetUnturnedAdmins && player.IsAdmin)
                {
                    continue;
                }

                if (IsPlayerProtectedFromRadiation(steamPlayer))
                {
                    continue;
                }

                EnsureRadiationEffect(player, steamPlayer);
            }
        }

        private void EnsureRadiationEffect(UnturnedPlayer player, SteamPlayer steamPlayer)
        {
            if (IsPlayerProtectedFromRadiation(steamPlayer))
            {
                return;
            }

            if (!Configuration.Instance.UseRadiationEffect || Configuration.Instance.RadiationEffectId == 0)
            {
                return;
            }

            if (Configuration.Instance.RespectOxygenSafeZones && IsPlayerInOxygenSafeZone(steamPlayer))
            {
                ClearRadiationEffectForPlayer(steamPlayer);
                return;
            }

            var steamId = steamPlayer.playerID.steamID;

            // Always re-apply effect to ensure it stays active
            // This is important because effects can expire or be cleared
            EffectManager.sendUIEffect(
                Configuration.Instance.RadiationEffectId,
                Configuration.Instance.RadiationEffectKey,
                steamPlayer.transportConnection,
                true);

            _effectRecipients.Add(steamId);
        }

        private void ApplyRadiationDamage(UnturnedPlayer player)
        {
            if (player == null || player.Dead || player.Player == null || player.Player.life == null)
            {
                return;
            }

            var damage = Configuration.Instance.InfectionDamagePerTick;
            if (damage <= 0)
            {
                return;
            }

            try
            {
                // Use askDamage for proper damage handling with death cause
                var position = player.Position;
                var deathCause = EDeathCause.INFECTION;
                var limb = ELimb.SPINE;
                var killer = CSteamID.Nil;
                EPlayerKill killType;
                var bypassSafezone = false; // Respect safezones
                var ragdollEffect = ERagdollEffect.NONE;
                var trackKill = false;
                var dropLoot = true;

                player.Player.life.askDamage(
                    damage,
                    position,
                    deathCause,
                    limb,
                    killer,
                    out killType,
                    bypassSafezone,
                    ragdollEffect,
                    trackKill,
                    dropLoot);
            }
            catch (Exception ex)
            {
                RocketLogger.LogWarning($"[RadiationStorm] Failed to apply radiation damage: {ex.Message}");
            }
        }

        private void ClearRadiationEffectForPlayer(SteamPlayer steamPlayer)
        {
            var steamId = steamPlayer.playerID.steamID;
            var removed = _effectRecipients.Remove(steamId);

            if (!removed)
            {
                return;
            }

            if (!Configuration.Instance.UseRadiationEffect || Configuration.Instance.RadiationEffectId == 0)
            {
                return;
            }

            EffectManager.askEffectClearByID(Configuration.Instance.RadiationEffectId, steamPlayer.transportConnection);
        }

        private void ClearRadiationEffectAll()
        {
            if (!Configuration.Instance.UseRadiationEffect || Configuration.Instance.RadiationEffectId == 0)
            {
                _effectRecipients.Clear();
                return;
            }

            foreach (var steamPlayer in Provider.clients)
            {
                EffectManager.askEffectClearByID(Configuration.Instance.RadiationEffectId, steamPlayer.transportConnection);
            }

            _effectRecipients.Clear();
        }

        private bool IsPlayerProtectedFromRadiation(SteamPlayer steamPlayer)
        {
            if (steamPlayer == null)
            {
                return false;
            }

            if (IsPlayerInSafezoneRadiator(steamPlayer))
            {
                ClearRadiationEffectForPlayer(steamPlayer);
                return true;
            }

            if (Configuration.Instance.RespectOxygenSafeZones && IsPlayerInOxygenSafeZone(steamPlayer))
            {
                ClearRadiationEffectForPlayer(steamPlayer);
                return true;
            }

            return false;
        }

        private bool IsPlayerInSafezoneRadiator(SteamPlayer steamPlayer)
        {
            if (!Configuration.Instance.RespectSafezoneRadiators || steamPlayer?.player == null)
            {
                return false;
            }

            UpdateSafezoneRadiatorCacheIfNeeded();

            if (_safezoneRadiators.Count == 0)
            {
                return false;
            }

            var position = steamPlayer.player.transform.position;

            for (var i = 0; i < _safezoneRadiators.Count; i++)
            {
                var info = _safezoneRadiators[i];
                if ((position - info.Position).sqrMagnitude <= info.SqrRadius)
                {
                    return true;
                }
            }

            return false;
        }

        private void UpdateSafezoneRadiatorCacheIfNeeded()
        {
            if (!Configuration.Instance.RespectSafezoneRadiators)
            {
                _safezoneRadiators.Clear();
                _safezoneRadiatorsCacheExpiryUtc = DateTime.MinValue;
                return;
            }

            var now = DateTime.UtcNow;
            if (now < _safezoneRadiatorsCacheExpiryUtc)
            {
                return;
            }

            _safezoneRadiatorsCacheExpiryUtc = now.AddSeconds(Math.Max(1.0, Configuration.Instance.SafezoneRadiatorRefreshSeconds));
            _safezoneRadiators.Clear();

            try
            {
                CollectSafezoneRadiators(_safezoneRadiators);
            }
            catch (Exception ex)
            {
                if (!_safezoneReflectionWarningLogged)
                {
                    _safezoneReflectionWarningLogged = true;
                    RocketLogger.LogWarning($"[RadiationStorm] Failed to refresh safezone radiators: {ex.Message}");
                }
            }
        }

        private void CollectSafezoneRadiators(List<SafezoneRadiatorInfo> buffer)
        {
            var ids = Configuration.Instance.SafezoneRadiatorItemIds;
            if (ids == null || ids.Count == 0)
            {
                return;
            }

            var regions = BarricadeManager.regions;
            if (regions != null)
            {
                var lengthX = regions.GetLength(0);
                var lengthY = regions.GetLength(1);

                for (var x = 0; x < lengthX; x++)
                {
                    for (var y = 0; y < lengthY; y++)
                    {
                        var region = regions[x, y];
                        if (region == null)
                        {
                            continue;
                        }

                        AddRadiatorsFromRegion(region, buffer, ids);
                    }
                }
            }

            var vehicleRegions = BarricadeManager.vehicleRegions;
            if (vehicleRegions != null)
            {
                foreach (var region in vehicleRegions)
                {
                    if (region == null)
                    {
                        continue;
                    }

                    AddRadiatorsFromRegion(region, buffer, ids);
                }
            }
        }

        private void AddRadiatorsFromRegion(BarricadeRegion region, List<SafezoneRadiatorInfo> buffer, IList<ushort> ids)
        {
            var drops = region.drops;
            if (drops == null)
            {
                return;
            }

            for (var i = 0; i < drops.Count; i++)
            {
                var drop = drops[i];
                if (drop == null)
                {
                    continue;
                }

                if (TryCreateSafezoneRadiatorInfo(drop, ids, out var info))
                {
                    buffer.Add(info);
                }
            }
        }

        private bool TryCreateSafezoneRadiatorInfo(BarricadeDrop drop, IList<ushort> ids, out SafezoneRadiatorInfo info)
        {
            info = default;

            if (drop == null)
            {
                return false;
            }

            // Access barricade asset via reflection
            ItemBarricadeAsset asset = null;
            try
            {
                // Try common property names
                var barricadeType = typeof(BarricadeDrop);
                var barricadeProp = barricadeType.GetProperty("barricade", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? barricadeType.GetProperty("serverAsset", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? barricadeType.GetProperty("asset", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (barricadeProp != null)
                {
                    var barricadeObj = barricadeProp.GetValue(drop);
                    if (barricadeObj != null)
                    {
                        var assetProp = barricadeObj.GetType().GetProperty("asset", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (assetProp != null)
                        {
                            asset = assetProp.GetValue(barricadeObj) as ItemBarricadeAsset;
                        }
                    }
                }

                // Fallback: try direct asset property on BarricadeDrop
                if (asset == null)
                {
                    var directAssetProp = barricadeType.GetProperty("asset", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (directAssetProp != null)
                    {
                        asset = directAssetProp.GetValue(drop) as ItemBarricadeAsset;
                    }
                }
            }
            catch
            {
                // Reflection failed, try alternative approach
            }

            if (asset == null)
            {
                return false;
            }

            if (!ids.Contains(asset.id))
            {
                return false;
            }

            var transform = drop.model ?? drop.interactable?.transform;
            if (transform == null)
            {
                return false;
            }

            var radius = (float)Configuration.Instance.SafezoneRadiatorDefaultRadius;
            var isPowered = true;

            var interactable = drop.interactable;
            if (interactable != null)
            {
                var cache = GetSafezoneReflectionCache(interactable.GetType());

                if (cache.TryGetRadius(interactable, out var detectedRadius) && detectedRadius > 0f)
                {
                    radius = detectedRadius;
                }

                if (Configuration.Instance.SafezoneRadiatorRequiresPower && cache.TryGetActive(interactable, out var active))
                {
                    isPowered = active;
                }
            }

            if (Configuration.Instance.SafezoneRadiatorRequiresPower && !isPowered)
            {
                return false;
            }

            if (radius <= 0f)
            {
                radius = (float)Configuration.Instance.SafezoneRadiatorDefaultRadius;
            }

            info = new SafezoneRadiatorInfo
            {
                Position = transform.position,
                Radius = radius,
                SqrRadius = radius * radius
            };

            return true;
        }

        private SafezoneReflectionCache GetSafezoneReflectionCache(Type type)
        {
            if (!_safezoneReflectionCache.TryGetValue(type, out var cache))
            {
                cache = BuildSafezoneReflectionCache(type);
                _safezoneReflectionCache[type] = cache;
            }

            return cache;
        }

        private SafezoneReflectionCache BuildSafezoneReflectionCache(Type type)
        {
            var cache = new SafezoneReflectionCache();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (var field in type.GetFields(flags))
            {
                var name = field.Name.ToLowerInvariant();
                if (cache.RadiusAccessor == null && name.Contains("radius"))
                {
                    cache.RadiusAccessor = obj => field.GetValue(obj);
                }

                if (cache.ActiveAccessor == null && (name.Contains("powered") || name.Contains("active") || name.Contains("enabled")))
                {
                    cache.ActiveAccessor = obj => field.GetValue(obj);
                }

                if (cache.IsComplete)
                {
                    break;
                }
            }

            if (!cache.IsComplete)
            {
                foreach (var property in type.GetProperties(flags))
                {
                    if (!property.CanRead)
                    {
                        continue;
                    }

                    var name = property.Name.ToLowerInvariant();
                    var getter = property.GetGetMethod(true);
                    if (getter == null)
                    {
                        continue;
                    }

                    if (cache.RadiusAccessor == null && name.Contains("radius"))
                    {
                        cache.RadiusAccessor = obj => getter.Invoke(obj, null);
                    }

                    if (cache.ActiveAccessor == null && (name.Contains("powered") || name.Contains("active") || name.Contains("enabled")))
                    {
                        cache.ActiveAccessor = obj => getter.Invoke(obj, null);
                    }

                    if (cache.IsComplete)
                    {
                        break;
                    }
                }
            }

            return cache;
        }

        private bool TryIsInsideSafezoneRadiator(SteamPlayer steamPlayer, out SafezoneRadiatorInfo info)
        {
            info = default;

            if (!Configuration.Instance.RespectSafezoneRadiators)
            {
                return false;
            }

            UpdateSafezoneRadiatorCacheIfNeeded();

            if (_safezoneRadiators.Count == 0)
            {
                return false;
            }

            var position = steamPlayer.player.transform.position;

            for (var i = 0; i < _safezoneRadiators.Count; i++)
            {
                var candidate = _safezoneRadiators[i];
                if ((position - candidate.Position).sqrMagnitude <= candidate.SqrRadius)
                {
                    info = candidate;
                    return true;
                }
            }

            return false;
        }

        private static float ConvertToFloat(object value, float fallback)
        {
            switch (value)
            {
                case null:
                    return fallback;
                case float f:
                    return f;
                case double d:
                    return (float)d;
                case int i:
                    return i;
                case uint ui:
                    return ui;
                case long l:
                    return l;
                case ulong ul:
                    return ul;
                case short s:
                    return s;
                case ushort us:
                    return us;
                case byte b:
                    return b;
                case sbyte sb:
                    return sb;
                case decimal dec:
                    return (float)dec;
            }

            if (float.TryParse(value.ToString(), out var parsed))
            {
                return parsed;
            }

            return fallback;
        }

        private static bool ConvertToBool(object value, bool fallback)
        {
            switch (value)
            {
                case null:
                    return fallback;
                case bool b:
                    return b;
                case byte bt:
                    return bt != 0;
                case sbyte sb:
                    return sb != 0;
                case int i:
                    return i != 0;
                case uint ui:
                    return ui != 0;
                case long l:
                    return l != 0L;
                case ulong ul:
                    return ul != 0UL;
            }

            if (bool.TryParse(value.ToString(), out var parsed))
            {
                return parsed;
            }

            return fallback;
        }

        private bool IsPlayerInOxygenSafeZone(SteamPlayer steamPlayer)
        {
            if (!Configuration.Instance.RespectOxygenSafeZones || steamPlayer?.player == null)
            {
                return false;
            }

            var position = steamPlayer.player.transform.position;
            return TryIsInsideOxygenSafeZone(position, out var alpha) && alpha >= Math.Max(0f, (float)Configuration.Instance.OxygenSafeAlphaThreshold);
        }

        private bool TryIsInsideOxygenSafeZone(Vector3 position, out float alpha)
        {
            alpha = 0f;

            if (OxygenVolumeManagerBreathableMethod == null)
            {
                LogOxygenReflectionWarningOnce("Unable to resolve OxygenVolumeManager.IsPositionInsideBreathableVolume.");
                return false;
            }

            object manager = null;

            try
            {
                manager = OxygenVolumeManagerInstanceProperty?.GetValue(null) ?? OxygenVolumeManagerInstanceField?.GetValue(null);
            }
            catch (Exception ex)
            {
                LogOxygenReflectionWarningOnce($"Failed to access OxygenVolumeManager instance: {ex.Message}");
                return false;
            }

            if (manager == null)
            {
                LogOxygenReflectionWarningOnce("OxygenVolumeManager instance not available.");
                return false;
            }

            var args = new object[] { position, 0f };

            try
            {
                var result = OxygenVolumeManagerBreathableMethod.Invoke(manager, args);
                alpha = (float)args[1];
                return result is bool inside && inside;
            }
            catch (Exception ex)
            {
                LogOxygenReflectionWarningOnce($"Oxygen safe zone check failed: {ex.Message}");
                return false;
            }
        }

        private void LogOxygenReflectionWarningOnce(string message)
        {
            if (_oxygenReflectionWarningLogged)
            {
                return;
            }

            _oxygenReflectionWarningLogged = true;
            RocketLogger.LogWarning($"[RadiationStorm] {message} Oxygen safe zones will be ignored.");
        }

        private void CreateDeadzone()
        {
            if (!Configuration.Instance.UseDeadzone)
            {
                return;
            }

            try
            {
                RemoveDeadzone();

                // First, try to find and enable existing deadzone nodes from level
                if (TryEnableExistingDeadzones())
                {
                    RocketLogger.Log("[RadiationStorm] Using existing deadzone nodes from level.");
                    return;
                }

                // If no existing deadzones found, try to create new one
                var mapSize = ResolveMapSize();
                var mapSizeInMeters = 512f * Mathf.Pow(2f, mapSize);
                var halfMapSize = mapSizeInMeters / 2f;
                var diagonalDistance = Mathf.Sqrt(2f) * halfMapSize;
                var radius = Mathf.Max((float)Configuration.Instance.DeadzoneRadius, diagonalDistance + 100f);

                var deadzoneNode = BuildDeadzoneNode(Vector3.zero, radius);
                if (deadzoneNode == null)
                {
                    RocketLogger.LogWarning("[RadiationStorm] Deadzone node could not be constructed. Deadzone will be skipped.");
                    return;
                }

                bool registered = false;
                
                // Try using registerDeadzone method first
                if (LevelNodesRegisterDeadzoneMethod != null)
                {
                    try
                    {
                        LevelNodesRegisterDeadzoneMethod.Invoke(null, new object[] { deadzoneNode });
                        _createdDeadzoneNode = deadzoneNode;
                        registered = true;
                        RocketLogger.Log("[RadiationStorm] Deadzone node registered using registerDeadzone method.");
                    }
                    catch (Exception ex)
                    {
                        RocketLogger.LogWarning($"[RadiationStorm] Failed to register deadzone using registerDeadzone method: {ex.Message}");
                    }
                }
                
                // Fallback: try to add directly to deadzoneNodes list
                if (!registered && LevelNodesDeadzoneNodesField != null)
                {
                    try
                    {
                        var deadzoneNodes = LevelNodesDeadzoneNodesField.GetValue(null);
                        if (deadzoneNodes is List<DeadzoneNode> nodes)
                        {
                            nodes.Add(deadzoneNode);
                            _createdDeadzoneNode = deadzoneNode;
                            registered = true;
                            RocketLogger.Log("[RadiationStorm] Deadzone node added directly to deadzoneNodes list.");
                        }
                        else
                        {
                            RocketLogger.LogWarning($"[RadiationStorm] deadzoneNodes field is not a List<DeadzoneNode>, it's: {deadzoneNodes?.GetType().Name ?? "null"}");
                        }
                    }
                    catch (Exception ex)
                    {
                        RocketLogger.LogWarning($"[RadiationStorm] Failed to add deadzone to list: {ex.Message}");
                    }
                }
                
                if (!registered)
                {
                    RocketLogger.LogWarning("[RadiationStorm] ⚠ Unable to create deadzone node dynamically.");
                    RocketLogger.LogWarning("[RadiationStorm] Deadzone nodes cannot be created at runtime in this Unturned version.");
                    RocketLogger.LogWarning("[RadiationStorm] Falling back to manual damage system.");
                    
                    // Ensure manual damage is enabled if deadzone fails
                    if (Configuration.Instance.InfectionDamagePerTick == 0)
                    {
                        RocketLogger.LogWarning("[RadiationStorm] ⚠ InfectionDamagePerTick is 0. Setting to default value of 5.");
                        RocketLogger.LogWarning("[RadiationStorm] Please set InfectionDamagePerTick > 0 in config for radiation damage to work.");
                    }
                    
                    return;
                }

                // Try to refresh deadzone system
                try
                {
                    if (LevelNodesRefreshDeadzonesMethod != null)
                    {
                        LevelNodesRefreshDeadzonesMethod.Invoke(null, null);
                        RocketLogger.Log("[RadiationStorm] Deadzone system refreshed.");
                    }
                    else if (LevelNodesLoadDeadzonesMethod != null)
                    {
                        LevelNodesLoadDeadzonesMethod.Invoke(null, null);
                        RocketLogger.Log("[RadiationStorm] Deadzone system reloaded.");
                    }
                }
                catch (Exception ex)
                {
                    RocketLogger.LogWarning($"[RadiationStorm] Failed to refresh deadzone system: {ex.Message}");
                }

                // Log deadzone node details for debugging
                RocketLogger.Log($"[RadiationStorm] Created deadzone node:");
                RocketLogger.Log($"  - Position: {deadzoneNode.point}");
                RocketLogger.Log($"  - Radius: {deadzoneNode.radius}");
                RocketLogger.Log($"  - Type: {deadzoneNode.type}");
                RocketLogger.Log($"  - Map size: {mapSizeInMeters}m x {mapSizeInMeters}m");
                
                // Verify node was added
                if (LevelNodesDeadzoneNodesField != null)
                {
                    try
                    {
                        var deadzoneNodes = LevelNodesDeadzoneNodesField.GetValue(null);
                        if (deadzoneNodes is List<DeadzoneNode> nodes)
                        {
                            RocketLogger.Log($"[RadiationStorm] Total deadzone nodes in system: {nodes.Count}");
                            if (nodes.Contains(deadzoneNode))
                            {
                                RocketLogger.Log("[RadiationStorm] ✓ Deadzone node confirmed in system.");
                            }
                            else
                            {
                                RocketLogger.LogWarning("[RadiationStorm] ✗ Deadzone node NOT found in system after registration!");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        RocketLogger.LogWarning($"[RadiationStorm] Failed to verify deadzone node: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                RocketLogger.LogWarning($"[RadiationStorm] Failed to create deadzone: {ex.Message}");
            }
        }

        private byte ResolveMapSize()
        {
            try
            {
                if (LevelSizeProperty != null)
                {
                    var sizeValue = LevelSizeProperty.GetValue(null);
                    if (sizeValue is byte sizeFromProperty)
                    {
                        return sizeFromProperty;
                    }
                }

                if (LevelSizeField != null)
                {
                    var sizeValue = LevelSizeField.GetValue(null);
                    if (sizeValue is byte sizeFromField)
                    {
                        return sizeFromField;
                    }
                }
            }
            catch
            {
                // ignored
            }

            return 4; // fallback for large maps
        }

        private DeadzoneNode BuildDeadzoneNode(Vector3 position, float radius)
        {
            try
            {
                var deadzoneNode = InstantiateDeadzoneNode();
                if (deadzoneNode == null)
                {
                    return null;
                }

                if (!TryAssignDeadzoneMember(deadzoneNode, position, "point", "_point", "m_Point"))
                {
                    RocketLogger.LogWarning("[RadiationStorm] Unable to assign position to DeadzoneNode via reflection.");
                    return null;
                }

                if (!TryAssignDeadzoneMember(deadzoneNode, radius, "radius", "_radius", "m_Radius"))
                {
                    try
                    {
                        deadzoneNode.radius = radius;
                    }
                    catch
                    {
                        RocketLogger.LogWarning("[RadiationStorm] Unable to assign radius to DeadzoneNode.");
                        return null;
                    }
                }

                var nodeTypeValue = GetEnumValue("SDG.Unturned.ENodeType", "DEADZONE");
                if (nodeTypeValue != null)
                {
                    TryAssignDeadzoneMember(deadzoneNode, nodeTypeValue, "type", "_type", "nodeType");
                }

                if (!TryAssignDeadzoneMember(deadzoneNode, EDeadzoneType.DefaultRadiation, "deadzoneType", "_deadzoneType", "m_DeadzoneType"))
                {
                    TryAssignDeadzoneMember(deadzoneNode, EDeadzoneType.DefaultRadiation, "deadzone", "_deadzone");
                }

                TryAssignDeadzoneMember(deadzoneNode, (float)Configuration.Instance.DeadzoneUnprotectedDamagePerSecond, "UnprotectedDamagePerSecond");
                TryAssignDeadzoneMember(deadzoneNode, (float)Configuration.Instance.DeadzoneProtectedDamagePerSecond, "ProtectedDamagePerSecond");
                TryAssignDeadzoneMember(deadzoneNode, (float)Configuration.Instance.DeadzoneUnprotectedRadiationPerSecond, "UnprotectedRadiationPerSecond");
                TryAssignDeadzoneMember(deadzoneNode, (float)Configuration.Instance.DeadzoneMaskFilterDamagePerSecond, "MaskFilterDamagePerSecond");

                return deadzoneNode;
            }
            catch (Exception ex)
            {
                RocketLogger.LogWarning($"[RadiationStorm] Failed to configure deadzone node: {ex.Message}");
                return null;
            }
        }

        private DeadzoneNode InstantiateDeadzoneNode()
        {
            var type = typeof(DeadzoneNode);

            try
            {
                foreach (var ctor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var parameters = ctor.GetParameters();
                    var args = new object[parameters.Length];

                    for (var i = 0; i < parameters.Length; i++)
                    {
                        var parameterType = parameters[i].ParameterType;
                        args[i] = parameterType.IsValueType ? Activator.CreateInstance(parameterType) : null;
                    }

                    try
                    {
                        return (DeadzoneNode)ctor.Invoke(args);
                    }
                    catch
                    {
                        // Try next constructor
                    }
                }

                return (DeadzoneNode)FormatterServices.GetUninitializedObject(type);
            }
            catch (Exception ex)
            {
                RocketLogger.LogWarning($"[RadiationStorm] Unable to instantiate DeadzoneNode: {ex.Message}");
                return null;
            }
        }

        private static bool TryAssignDeadzoneMember(object instance, object value, params string[] memberNames)
        {
            foreach (var memberName in memberNames)
            {
                if (TryAssignDeadzoneMemberInternal(instance, memberName, value))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryAssignDeadzoneMemberInternal(object instance, string memberName, object value)
        {
            var currentType = instance.GetType();

            while (currentType != null)
            {
                var property = currentType.GetProperty(memberName, DeadzoneReflectionFlags);
                if (property != null)
                {
                    var setter = property.GetSetMethod(true);
                    if (setter != null)
                    {
                        var convertedValue = ConvertDeadzoneMemberValue(value, property.PropertyType);
                        setter.Invoke(instance, new[] { convertedValue });
                        return true;
                    }
                }

                var field = currentType.GetField(memberName, DeadzoneReflectionFlags);
                if (field != null)
                {
                    var convertedValue = ConvertDeadzoneMemberValue(value, field.FieldType);
                    field.SetValue(instance, convertedValue);
                    return true;
                }

                currentType = currentType.BaseType;
            }

            return false;
        }

        private static object ConvertDeadzoneMemberValue(object value, Type targetType)
        {
            if (value == null)
            {
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }

            var valueType = value.GetType();
            if (targetType.IsAssignableFrom(valueType))
            {
                return value;
            }

            if (targetType.IsEnum)
            {
                if (valueType.IsEnum)
                {
                    var underlying = Convert.ChangeType(value, Enum.GetUnderlyingType(valueType));
                    return Enum.ToObject(targetType, underlying);
                }

                if (value is string enumName)
                {
                    return Enum.Parse(targetType, enumName, true);
                }

                if (value is IConvertible convertible)
                {
                    var underlyingType = Enum.GetUnderlyingType(targetType);
                    var numericValue = Convert.ChangeType(convertible, underlyingType);
                    return Enum.ToObject(targetType, numericValue);
                }

                return Enum.Parse(targetType, value.ToString(), true);
            }

            if (typeof(IConvertible).IsAssignableFrom(targetType) && value is IConvertible convertibleValue)
            {
                return Convert.ChangeType(convertibleValue, targetType);
            }

            return value;
        }

        private static object GetEnumValue(string typeName, string valueName)
        {
            try
            {
                var type = Type.GetType($"{typeName}, Assembly-CSharp") ?? Type.GetType(typeName);
                type ??= typeof(DeadzoneNode).Assembly.GetType(typeName);

                if (type == null || !type.IsEnum)
                {
                    return null;
                }

                return Enum.Parse(type, valueName, true);
            }
            catch
            {
                return null;
            }
        }

        private bool TryEnableExistingDeadzones()
        {
            try
            {
                RocketLogger.Log("[RadiationStorm] Attempting to find existing deadzone nodes...");
                
                // Method 1: Try LevelNodes.deadzoneNodes field
                if (LevelNodesDeadzoneNodesField != null)
                {
                    try
                    {
                        var deadzoneNodes = LevelNodesDeadzoneNodesField.GetValue(null);
                        if (deadzoneNodes is List<DeadzoneNode> nodes && nodes.Count > 0)
                        {
                            RocketLogger.Log($"[RadiationStorm] Found {nodes.Count} deadzone nodes via LevelNodes.deadzoneNodes");
                            return EnableDeadzoneNodes(nodes);
                        }
                    }
                    catch (Exception ex)
                    {
                        RocketLogger.LogWarning($"[RadiationStorm] Failed to access LevelNodes.deadzoneNodes: {ex.Message}");
                    }
                }
                
                // Method 2: Try Level class properties/fields
                RocketLogger.Log("[RadiationStorm] Trying Level class...");
                try
                {
                    var levelType = typeof(Level);
                    var levelFields = levelType.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    var levelProperties = levelType.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    
                    foreach (var field in levelFields)
                    {
                        if (field.FieldType == typeof(List<DeadzoneNode>) || 
                            (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(List<>) && 
                             field.FieldType.GetGenericArguments()[0] == typeof(DeadzoneNode)))
                        {
                            RocketLogger.Log($"  ✓ Found potential deadzone nodes in Level: {field.Name}");
                            try
                            {
                                var nodes = field.GetValue(null) as List<DeadzoneNode>;
                                if (nodes != null && nodes.Count > 0)
                                {
                                    RocketLogger.Log($"[RadiationStorm] Found {nodes.Count} deadzone nodes via Level.{field.Name}");
                                    return EnableDeadzoneNodes(nodes);
                                }
                            }
                            catch (Exception ex)
                            {
                                RocketLogger.LogWarning($"[RadiationStorm] Failed to access Level.{field.Name}: {ex.Message}");
                            }
                        }
                    }
                    
                    foreach (var prop in levelProperties)
                    {
                        if (prop.PropertyType == typeof(List<DeadzoneNode>) || 
                            (prop.PropertyType.IsGenericType && prop.PropertyType.GetGenericTypeDefinition() == typeof(List<>) && 
                             prop.PropertyType.GetGenericArguments()[0] == typeof(DeadzoneNode)))
                        {
                            RocketLogger.Log($"  ✓ Found potential deadzone nodes property in Level: {prop.Name}");
                            try
                            {
                                var nodes = prop.GetValue(null) as List<DeadzoneNode>;
                                if (nodes != null && nodes.Count > 0)
                                {
                                    RocketLogger.Log($"[RadiationStorm] Found {nodes.Count} deadzone nodes via Level.{prop.Name}");
                                    return EnableDeadzoneNodes(nodes);
                                }
                            }
                            catch (Exception ex)
                            {
                                RocketLogger.LogWarning($"[RadiationStorm] Failed to access Level.{prop.Name}: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    RocketLogger.LogWarning($"[RadiationStorm] Failed to search Level class: {ex.Message}");
                }
                
                // Method 3: Try LevelNodes - scan all fields
                RocketLogger.LogWarning("[RadiationStorm] ⚠ LevelNodes.deadzoneNodes field not found via reflection.");
                RocketLogger.LogWarning("[RadiationStorm] Trying alternative methods...");
                
                var levelNodesType = typeof(LevelNodes);
                var allFields = levelNodesType.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                RocketLogger.Log($"[RadiationStorm] Found {allFields.Length} static fields in LevelNodes:");
                foreach (var field in allFields)
                {
                    RocketLogger.Log($"  - {field.Name} ({field.FieldType.Name})");
                    if (field.FieldType == typeof(List<DeadzoneNode>) || 
                        (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(List<>) && 
                         field.FieldType.GetGenericArguments()[0] == typeof(DeadzoneNode)))
                    {
                        RocketLogger.Log($"  ✓ Found potential deadzone nodes field: {field.Name}");
                        try
                        {
                            var nodes = field.GetValue(null);
                            if (nodes is List<DeadzoneNode> deadzoneList && deadzoneList.Count > 0)
                            {
                                RocketLogger.Log($"[RadiationStorm] Found {deadzoneList.Count} deadzone nodes via field '{field.Name}'");
                                return EnableDeadzoneNodes(deadzoneList);
                            }
                        }
                        catch (Exception ex)
                        {
                            RocketLogger.LogWarning($"[RadiationStorm] Failed to access field '{field.Name}': {ex.Message}");
                        }
                    }
                }
                
                RocketLogger.LogWarning("[RadiationStorm] No deadzone nodes found through any method.");
                return false;
            }
            catch (Exception ex)
            {
                RocketLogger.LogWarning($"[RadiationStorm] Failed to enable existing deadzones: {ex.Message}");
                RocketLogger.LogWarning($"[RadiationStorm] Stack trace: {ex.StackTrace}");
                return false;
            }
        }
        
        private bool EnableDeadzoneNodes(List<DeadzoneNode> nodes)
        {
            try
            {
                _existingDeadzoneNodes = new List<DeadzoneNode>();
                _originalDeadzoneRadii = new Dictionary<DeadzoneNode, float>();

                var mapSize = ResolveMapSize();
                var mapSizeInMeters = 512f * Mathf.Pow(2f, mapSize);
                var halfMapSize = mapSizeInMeters / 2f;
                var diagonalDistance = Mathf.Sqrt(2f) * halfMapSize;
                var targetRadius = Mathf.Max((float)Configuration.Instance.DeadzoneRadius, diagonalDistance + 100f);

                foreach (var node in nodes)
                {
                    try
                    {
                        RocketLogger.Log($"[RadiationStorm] Processing deadzone node at {node.point}, current radius: {node.radius}");
                        
                        // Store original radius
                        _originalDeadzoneRadii[node] = node.radius;

                        // Enable deadzone by setting radius to target radius (or expand existing radius)
                        var newRadius = Mathf.Max(node.radius, targetRadius);
                        
                        try
                        {
                            node.radius = newRadius;
                            RocketLogger.Log($"[RadiationStorm] ✓ Successfully set radius to {node.radius}m");
                        }
                        catch (Exception ex)
                        {
                            RocketLogger.LogWarning($"[RadiationStorm] ✗ Failed to set radius: {ex.Message}");
                            continue;
                        }

                        // Update damage properties if possible
                        TryAssignDeadzoneMember(node, (float)Configuration.Instance.DeadzoneUnprotectedDamagePerSecond, "UnprotectedDamagePerSecond");
                        TryAssignDeadzoneMember(node, (float)Configuration.Instance.DeadzoneProtectedDamagePerSecond, "ProtectedDamagePerSecond");
                        TryAssignDeadzoneMember(node, (float)Configuration.Instance.DeadzoneUnprotectedRadiationPerSecond, "UnprotectedRadiationPerSecond");
                        TryAssignDeadzoneMember(node, (float)Configuration.Instance.DeadzoneMaskFilterDamagePerSecond, "MaskFilterDamagePerSecond");

                        _existingDeadzoneNodes.Add(node);
                        RocketLogger.Log($"[RadiationStorm] ✓ Enabled existing deadzone node at {node.point} with radius {node.radius}m");
                    }
                    catch (Exception ex)
                    {
                        RocketLogger.LogWarning($"[RadiationStorm] Failed to enable deadzone node at {node.point}: {ex.Message}");
                    }
                }

                if (_existingDeadzoneNodes.Count > 0)
                {
                    RocketLogger.Log($"[RadiationStorm] ✓ Successfully enabled {_existingDeadzoneNodes.Count} existing deadzone node(s).");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                RocketLogger.LogWarning($"[RadiationStorm] Failed to enable deadzone nodes: {ex.Message}");
                return false;
            }
        }

        private void RemoveDeadzone()
        {
            // Restore original radii for existing deadzones
            if (_existingDeadzoneNodes != null && _originalDeadzoneRadii != null)
            {
                foreach (var node in _existingDeadzoneNodes)
                {
                    try
                    {
                        if (_originalDeadzoneRadii.TryGetValue(node, out var originalRadius))
                        {
                            node.radius = originalRadius;
                            RocketLogger.Log($"[RadiationStorm] Restored deadzone node at {node.point} to original radius {originalRadius}m");
                        }
                    }
                    catch (Exception ex)
                    {
                        RocketLogger.LogWarning($"[RadiationStorm] Failed to restore deadzone node: {ex.Message}");
                    }
                }

                _existingDeadzoneNodes = null;
                _originalDeadzoneRadii = null;
            }

            // Remove created deadzone node if any
            if (_createdDeadzoneNode == null)
            {
                return;
            }

            try
            {
                // Remove deadzone using reflection
                if (LevelNodesRemoveDeadzoneMethod != null)
                {
                    LevelNodesRemoveDeadzoneMethod.Invoke(null, new object[] { _createdDeadzoneNode });
                }
                else
                {
                    // Fallback: try to remove directly from deadzoneNodes list
                    if (LevelNodesDeadzoneNodesField != null)
                    {
                        var deadzoneNodes = LevelNodesDeadzoneNodesField.GetValue(null);
                        if (deadzoneNodes is List<DeadzoneNode> nodes)
                        {
                            nodes.Remove(_createdDeadzoneNode);
                        }
                    }
                }

                RocketLogger.Log("[RadiationStorm] Removed created deadzone node.");
                _createdDeadzoneNode = null;
            }
            catch (Exception ex)
            {
                RocketLogger.LogWarning($"[RadiationStorm] Failed to remove deadzone: {ex.Message}");
            }
        }

        private void OnPlayerConnected(CSteamID steamId)
        {
            // Apply radiation effect to newly connected players if storm is active
            if (!_stormActive || !_damageActive)
            {
                return;
            }

            TaskDispatcher.QueueOnMainThread(() =>
            {
                try
                {
                    var steamPlayer = PlayerTool.getSteamPlayer(steamId);
                    if (steamPlayer == null)
                    {
                        return;
                    }

                    var player = UnturnedPlayer.FromSteamPlayer(steamPlayer);
                    if (player == null || player.Dead)
                    {
                        return;
                    }

                    if (!Configuration.Instance.TargetUnturnedAdmins && player.IsAdmin)
                    {
                        return;
                    }

                    if (IsPlayerProtectedFromRadiation(steamPlayer))
                    {
                        return;
                    }

                    // Apply effect to newly connected player
                    EnsureRadiationEffect(player, steamPlayer);
                }
                catch (Exception ex)
                {
                    RocketLogger.LogWarning($"[RadiationStorm] Failed to apply effect to newly connected player: {ex.Message}");
                }
            });
        }

        private void ExecuteWeatherCommand(string command)
        {
            try
            {
                // Try direct Commander execute first
                if (Rocket.Core.R.Commands != null)
                {
                    Rocket.Core.R.Commands.Execute(new Rocket.API.ConsolePlayer(), command);
                    return;
                }

                // Fallback to reflection
                _weatherCommandMethod ??= typeof(CommandWindow).GetMethod(
                    "executeCommand",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(string) },
                    null)
                    ?? typeof(CommandWindow).GetMethod(
                        "inputted",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        new[] { typeof(string) },
                        null);

                if (_weatherCommandMethod == null)
                {
                    RocketLogger.LogWarning("[RadiationStorm] Unable to locate weather command dispatcher.");
                    return;
                }

                _weatherCommandMethod.Invoke(null, new object[] { command });
            }
            catch (Exception ex)
            {
                RocketLogger.LogWarning($"[RadiationStorm] Weather command failed: {ex.Message}");
            }
        }

        private sealed class SafezoneReflectionCache
        {
            public Func<object, object> RadiusAccessor;
            public Func<object, object> ActiveAccessor;

            public bool TryGetRadius(object instance, out float radius)
            {
                if (RadiusAccessor != null)
                {
                    radius = ConvertToFloat(RadiusAccessor(instance), 0f);
                    return true;
                }

                radius = 0f;
                return false;
            }

            public bool TryGetActive(object instance, out bool isActive)
            {
                if (ActiveAccessor != null)
                {
                    isActive = ConvertToBool(ActiveAccessor(instance), true);
                    return true;
                }

                isActive = true;
                return false;
            }

            public bool IsComplete => RadiusAccessor != null && ActiveAccessor != null;
        }

        private struct SafezoneRadiatorInfo
        {
            public Vector3 Position;
            public float Radius;
            public float SqrRadius;
        }
    }
}

