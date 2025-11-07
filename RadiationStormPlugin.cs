extern alias SteamworksNET;

using System;
using System.Collections.Generic;
using System.Reflection;
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
        private DeadzoneNode _createdDeadzoneNode;
        private static readonly FieldInfo LevelNodesDeadzoneNodesField = typeof(LevelNodes).GetField("deadzoneNodes", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo LevelNodesRegisterDeadzoneMethod = typeof(LevelNodes).GetMethod("registerDeadzone", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo LevelNodesRemoveDeadzoneMethod = typeof(LevelNodes).GetMethod("removeDeadzone", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo LevelSizeField = typeof(Level).GetField("size", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly PropertyInfo LevelSizeProperty = typeof(Level).GetProperty("size", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

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

            if (Configuration.Instance.UseRadiationEffect)
            {
                ApplyRadiationEffectAll();
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
                // Remove existing deadzone if any
                RemoveDeadzone();

                // Get map size to calculate appropriate radius
                byte mapSize = 0;
                try
                {
                    if (LevelSizeProperty != null)
                    {
                        var sizeValue = LevelSizeProperty.GetValue(null);
                        if (sizeValue is byte b)
                        {
                            mapSize = b;
                        }
                    }
                    else if (LevelSizeField != null)
                    {
                        var sizeValue = LevelSizeField.GetValue(null);
                        if (sizeValue is byte b)
                        {
                            mapSize = b;
                        }
                    }
                }
                catch
                {
                    // Fallback: use default map size (usually 4 for large maps)
                    mapSize = 4;
                }

                // Calculate map bounds based on size
                // Map size: 0=small(512m), 1=medium(1024m), 2=large(2048m), 3=insane(4096m), 4=extreme(8192m)
                float mapSizeInMeters = 512f * Mathf.Pow(2f, mapSize);
                
                // Calculate radius needed to cover entire map (diagonal distance from center to corner)
                // Using diagonal: sqrt(2) * halfMapSize, plus some buffer
                float halfMapSize = mapSizeInMeters / 2f;
                float diagonalDistance = Mathf.Sqrt(2f) * halfMapSize;
                float radius = Mathf.Max((float)Configuration.Instance.DeadzoneRadius, diagonalDistance + 100f); // Add 100m buffer

                // Create single deadzone node at map center (0, 0, 0) with calculated radius
                var deadzoneNode = new DeadzoneNode
                {
                    point = Vector3.zero, // Center of map
                    radius = radius,
                    type = EDeadzoneType.DefaultRadiation
                };

                // Set damage properties using reflection
                var nodeType = typeof(DeadzoneNode);
                var unprotectedDamageProp = nodeType.GetProperty("UnprotectedDamagePerSecond", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var protectedDamageProp = nodeType.GetProperty("ProtectedDamagePerSecond", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var unprotectedRadiationProp = nodeType.GetProperty("UnprotectedRadiationPerSecond", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var maskFilterDamageProp = nodeType.GetProperty("MaskFilterDamagePerSecond", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (unprotectedDamageProp != null && unprotectedDamageProp.CanWrite)
                {
                    unprotectedDamageProp.SetValue(deadzoneNode, (float)Configuration.Instance.DeadzoneUnprotectedDamagePerSecond);
                }

                if (protectedDamageProp != null && protectedDamageProp.CanWrite)
                {
                    protectedDamageProp.SetValue(deadzoneNode, (float)Configuration.Instance.DeadzoneProtectedDamagePerSecond);
                }

                if (unprotectedRadiationProp != null && unprotectedRadiationProp.CanWrite)
                {
                    unprotectedRadiationProp.SetValue(deadzoneNode, (float)Configuration.Instance.DeadzoneUnprotectedRadiationPerSecond);
                }

                if (maskFilterDamageProp != null && maskFilterDamageProp.CanWrite)
                {
                    maskFilterDamageProp.SetValue(deadzoneNode, (float)Configuration.Instance.DeadzoneMaskFilterDamagePerSecond);
                }

                // Register deadzone using reflection
                if (LevelNodesRegisterDeadzoneMethod != null)
                {
                    LevelNodesRegisterDeadzoneMethod.Invoke(null, new object[] { deadzoneNode });
                    _createdDeadzoneNode = deadzoneNode;
                    RocketLogger.Log($"[RadiationStorm] Created single deadzone node covering entire map ({mapSizeInMeters}m x {mapSizeInMeters}m) with radius {radius}m.");
                }
                else
                {
                    // Fallback: try to add directly to deadzoneNodes list
                    if (LevelNodesDeadzoneNodesField != null)
                    {
                        var deadzoneNodes = LevelNodesDeadzoneNodesField.GetValue(null);
                        if (deadzoneNodes is List<DeadzoneNode> nodes)
                        {
                            nodes.Add(deadzoneNode);
                            _createdDeadzoneNode = deadzoneNode;
                            RocketLogger.Log($"[RadiationStorm] Created single deadzone node covering entire map ({mapSizeInMeters}m x {mapSizeInMeters}m) with radius {radius}m.");
                        }
                        else
                        {
                            RocketLogger.LogWarning("[RadiationStorm] Unable to access deadzoneNodes list. Deadzone creation failed.");
                        }
                    }
                    else
                    {
                        RocketLogger.LogWarning("[RadiationStorm] Unable to locate deadzone registration methods. Deadzone creation failed.");
                    }
                }
            }
            catch (Exception ex)
            {
                RocketLogger.LogWarning($"[RadiationStorm] Failed to create deadzone: {ex.Message}");
            }
        }

        private void RemoveDeadzone()
        {
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

                RocketLogger.Log("[RadiationStorm] Removed deadzone node.");
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

