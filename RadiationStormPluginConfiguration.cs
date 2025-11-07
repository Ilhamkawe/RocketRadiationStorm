using System.Collections.Generic;
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
        public bool RespectOxygenSafeZones { get; set; }
        public double OxygenSafeAlphaThreshold { get; set; }
        public bool RespectSafezoneRadiators { get; set; }
        public List<ushort> SafezoneRadiatorItemIds { get; set; }
        public double SafezoneRadiatorDefaultRadius { get; set; }
        public double SafezoneRadiatorRefreshSeconds { get; set; }
        public bool SafezoneRadiatorRequiresPower { get; set; }

        public void LoadDefaults()
        {
            TickIntervalSeconds = 2.0; // Check effect tiap 2 detik
            InfectionDamagePerTick = 0; // 0 = damage dari effect, bukan manual
            BroadcastMessages = true;
            TargetUnturnedAdmins = false;
            AutoStormEnabled = true; // Auto storm aktif
            AutoStormMinIntervalMinutes = 30; // Minimal 30 menit
            AutoStormMaxIntervalMinutes = 60; // Maksimal 60 menit
            StormDurationSeconds = 300; // Storm berlangsung 5 menit
            UseWeather = true; // Aktifkan weather
            WeatherGuid = "6c850687bdb947a689fa8de8a8d99afb"; // Default fog GUID
            WeatherDamageDelaySeconds = 5; // Delay 5 detik setelah weather
            UseRadiationEffect = true; // Aktifkan deadzone effect
            RadiationEffectId = 14780; // Deadzone effect (tengkorak + damage)
            RadiationEffectKey = 514;
            RespectOxygenSafeZones = false;
            OxygenSafeAlphaThreshold = 0.05; // Minimal alpha untuk dianggap aman (5%)
            RespectSafezoneRadiators = true;
            SafezoneRadiatorItemIds = new List<ushort> { 1242 }; // Safezone Radiator bawaan
            SafezoneRadiatorDefaultRadius = 8.0; // Radius asli sekitar 8 meter
            SafezoneRadiatorRefreshSeconds = 5.0;
            SafezoneRadiatorRequiresPower = true;
        }
    }
}

