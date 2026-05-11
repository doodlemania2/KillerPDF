using System.Configuration;

namespace KillerPDF.Properties
{
    internal sealed class Settings : ApplicationSettingsBase
    {
        private static readonly Settings defaultInstance = (Settings)Synchronized(new Settings());

        public static Settings Default => defaultInstance;

        [UserScopedSetting]
        [DefaultSettingValue("1")]
        public double LastZoomLevel
        {
            get => (double)this[nameof(LastZoomLevel)];
            set => this[nameof(LastZoomLevel)] = value;
        }
    }
}
