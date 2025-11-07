using Rocket.API;

namespace RocketRadiationStorm
{
    public class RadiationStormPluginConfiguration : IRocketPluginConfiguration
    {
        public double TickIntervalSeconds { get; set; }
        public byte InfectionDamagePerTick { get; set; }
        public bool BroadcastMessages { get; set; }
        public bool TargetUnturnedAdmins { get; set; }

        public void LoadDefaults()
        {
            TickIntervalSeconds = 2.0;
            InfectionDamagePerTick = 5;
            BroadcastMessages = true;
            TargetUnturnedAdmins = false;
        }
    }
}

