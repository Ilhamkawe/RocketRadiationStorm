using System.ComponentModel;
using Rocket.API;

namespace RocketRadiationStorm
{
    public class RadiationStormPluginConfiguration : IRocketPluginConfiguration
    {
        public double TickIntervalSeconds { get; set; }
        public byte InfectionDamagePerTick { get; set; }
        public bool BroadcastMessages { get; set; }
        public bool TargetUnturnedAdmins { get; set; }
        public bool AutoStormEnabled { get; set; }
        public double AutoStormMinIntervalMinutes { get; set; }
        public double AutoStormMaxIntervalMinutes { get; set; }
        public double StormDurationSeconds { get; set; }
        public bool UseWeather { get; set; }
        
        [DefaultValue("00000000000000000000000000000000")]
        public string WeatherGuid { get; set; }
        
        public double WeatherDamageDelaySeconds { get; set; }
        public bool UseRadiationEffect { get; set; }
        public ushort RadiationEffectId { get; set; }
        public short RadiationEffectKey { get; set; }

        public void LoadDefaults()
        {
            TickIntervalSeconds = 2.0;
            InfectionDamagePerTick = 5;
            BroadcastMessages = true;
            TargetUnturnedAdmins = false;
            AutoStormEnabled = false;
            AutoStormMinIntervalMinutes = 20;
            AutoStormMaxIntervalMinutes = 30;
            StormDurationSeconds = 180;
            UseWeather = false;
            WeatherGuid = "00000000000000000000000000000000";
            WeatherDamageDelaySeconds = 5;
            UseRadiationEffect = false;
            RadiationEffectId = 0;
            RadiationEffectKey = 514;
        }
    }
}

