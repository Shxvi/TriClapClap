using System.Text.Json;
using System.IO;
using System;

namespace triclapclap.Core
{
    public class AppConfig
    {
        public string TargetKeys { get; set; } = "Z,X";
        public bool ListenToAllKeys { get; set; } = false;
        public int AnimationDelayMs { get; set; } = 40;
        public string ChromaKeyColor { get; set; } = "#00FF00";
        public string FontColor { get; set; } = "#FFFFFF";
        public string FontName { get; set; } = "Arial";
        public int FontSize { get; set; } = 28;
        public int TextX { get; set; } = 20;
        public int TextY { get; set; } = 20;
        public bool IsSoundLocalEnabled { get; set; } = true;
        public bool IsSoundStreamEnabled { get; set; } = true;
        public bool IsOutlineEnabled { get; set; } = true;
        public string OutlineColor { get; set; } = "#000000";
        public int OutlineSize { get; set; } = 2;
        public bool IsTextAboveAssets { get; set; } = false;
        public int WsPort { get; set; } = 8080;
        public string TextFormat { get; set; } = "hits {0}\ncps {1}";
        public bool IsClickThrough { get; set; } = false;

        public static AppConfig Load(string path)
        {
            if (File.Exists(path))
            {
                try
                {
                    var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();
                    if (cfg.TextFormat != null) cfg.TextFormat = cfg.TextFormat.Replace("\\n", "\n");
                    return cfg;
                }
                catch { }
            }
            var newCfg = new AppConfig();
            Save(path, newCfg);
            return newCfg;
        }

        public static void Save(string path, AppConfig config)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}