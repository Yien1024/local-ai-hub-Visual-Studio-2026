using System;
using System.IO;
using System.Text;

namespace LocalAIHub.Core
{
    /// <summary>
    /// 设置持久化：%LOCALAPPDATA%\LocalAIHub\settings.json
    /// 不用 VS 的设置存储，因为要能在没有 VS 的测试宿主里也跑同一份逻辑。
    /// </summary>
    public static class SettingsStore
    {
        public static string DirectoryPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LocalAIHub");
            }
        }

        public static string FilePath { get { return Path.Combine(DirectoryPath, "settings.json"); } }

        public static HubSettings Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new HubSettings();
                var text = File.ReadAllText(FilePath, Encoding.UTF8);
                return HubSettings.FromJson(Json.ParseObject(text));
            }
            catch (Exception ex)
            {
                Log.Error("SettingsStore.Load", ex);
                return new HubSettings();
            }
        }

        public static bool Save(HubSettings s)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                File.WriteAllText(FilePath, Json.Serialize(s.ToJson(), true), new UTF8Encoding(false));
                Log.Info("设置已保存: " + FilePath);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("SettingsStore.Save", ex);
                return false;
            }
        }
    }
}
