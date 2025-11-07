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

        public static RadiationStormPlugin Instance { get; private set; }

        protected override void Load()
        {
            Instance = this;
            _stormActive = false;
            _damageActive = false;

            InitializeTickTimer();
            InitializeAutoStormScheduler();

            RocketLogger.Log("[RadiationStorm] Plugin loaded.");
        }

        protected override void Unload()
        {
            StopTickTimer();
            StopAutoStormTimer();
            StopStormDurationTimer();
            StopDamageDelayTimer();
            ClearRadiationEffectAll();
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

                EnsureRadiationEffect(player, steamPlayer);

                var currentInfection = player.Infection;

                if (currentInfection >= 100)
                {
                    continue;
                }

                var damage = Configuration.Instance.InfectionDamagePerTick;

                if (damage <= 0)
                {
                    continue;
                }

                var newValue = (byte)Math.Min(100, currentInfection + damage);

                if (newValue == currentInfection)
                {
                    continue;
                }

                player.Infection = newValue;
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
                ExecuteWeatherCommand($"weather add {Configuration.Instance.WeatherGuid}");
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
                ExecuteWeatherCommand($"weather remove {Configuration.Instance.WeatherGuid}");
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

                EnsureRadiationEffect(player, steamPlayer);
            }
        }

        private void EnsureRadiationEffect(UnturnedPlayer player, SteamPlayer steamPlayer)
        {
            if (!Configuration.Instance.UseRadiationEffect || Configuration.Instance.RadiationEffectId == 0)
            {
                return;
            }

            var steamId = steamPlayer.playerID.steamID;

            if (_effectRecipients.Contains(steamId))
            {
                return;
            }

            EffectManager.sendUIEffect(
                Configuration.Instance.RadiationEffectId,
                Configuration.Instance.RadiationEffectKey,
                steamPlayer.transportConnection,
                true);

            _effectRecipients.Add(steamId);
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

        private void ExecuteWeatherCommand(string command)
        {
            try
            {
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
    }
}

