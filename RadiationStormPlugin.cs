using System;
using System.Timers;
using Rocket.API.Collections;
using Rocket.Core.Logging;
using Rocket.Core.Plugins;
using Rocket.Core.Utils;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;
using UnityEngine;

namespace RocketRadiationStorm
{
    public class RadiationStormPlugin : RocketPlugin<RadiationStormPluginConfiguration>
    {
        private Timer _tickTimer;
        private bool _stormActive;

        public static RadiationStormPlugin Instance { get; private set; }

        protected override void Load()
        {
            Instance = this;
            _stormActive = false;

            InitializeTimer();

            Logger.Log("[RadiationStorm] Plugin loaded.");
        }

        protected override void Unload()
        {
            StopTimer();
            Instance = null;
            Logger.Log("[RadiationStorm] Plugin unloaded.");
        }

        public override TranslationList DefaultTranslations => new TranslationList
        {
            { "storm_start", "Radiation storm has begun!" },
            { "storm_stop", "Radiation storm has ended." },
            { "storm_already_active", "Radiation storm is already active." },
            { "storm_not_active", "No active radiation storm." },
            { "storm_status", "Radiation storm is currently {0}." }
        };

        private void InitializeTimer()
        {
            StopTimer();

            var interval = Math.Max(0.5, Configuration.Instance.TickIntervalSeconds);
            _tickTimer = new Timer(interval * 1000);
            _tickTimer.AutoReset = true;
            _tickTimer.Elapsed += OnTimerElapsed;
            _tickTimer.Start();
        }

        private void StopTimer()
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
            if (!_stormActive)
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

                var currentInfection = player.Infection;

                if (currentInfection == 0)
                {
                    continue;
                }

                var damage = Configuration.Instance.InfectionDamagePerTick;

                if (damage <= 0)
                {
                    continue;
                }

                var newValue = (byte)Math.Max(0, currentInfection - damage);

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
            Broadcast("storm_start");
        }

        public void StopStorm()
        {
            if (!_stormActive)
            {
                throw new InvalidOperationException(Translate("storm_not_active"));
            }

            _stormActive = false;
            Broadcast("storm_stop");
        }

        public bool StormActive => _stormActive;

        private void Broadcast(string translationKey)
        {
            if (!Configuration.Instance.BroadcastMessages)
            {
                return;
            }

            UnturnedChat.Say(Translate(translationKey), Color.green);
        }
    }
}

