using System.Text.Json;
using System.Text.Json.Nodes;

namespace AmllTtmlTool.Core;

public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private JsonObject _data = [];

    public SettingsStore(string path)
    {
        _path = path;
        Reload();
    }

    public string Path => _path;

    public void Reload()
    {
        lock (_gate)
        {
            _data = LoadFile(_path);
        }
    }

    public string GetString(string key, string fallback)
    {
        lock (_gate)
        {
            if (_data.TryGetPropertyValue(key, out var node)
                && node is JsonValue value
                && value.TryGetValue<string>(out var text)
                && text is not null)
            {
                return text;
            }
            return fallback;
        }
    }

    public void SetString(string key, string value)
    {
        lock (_gate)
        {
            _data[key] = value;
            SaveFile();
        }
    }

    public bool GetBool(string key, bool fallback)
    {
        lock (_gate)
        {
            if (_data.TryGetPropertyValue(key, out var node)
                && node is JsonValue value
                && value.TryGetValue<bool>(out var flag))
            {
                return flag;
            }
            return fallback;
        }
    }

    public void SetBool(string key, bool value)
    {
        lock (_gate)
        {
            _data[key] = value;
            SaveFile();
        }
    }

    // Invariant culture both ways: a user who travels between locales must
    // not come back to 12.5 stored as 12,5 read as 12.
    public double GetDouble(string key, double fallback)
    {
        lock (_gate)
        {
            if (_data.TryGetPropertyValue(key, out var node)
                && node is JsonValue value
                && value.TryGetValue<double>(out var number)
                && !double.IsNaN(number) && !double.IsInfinity(number))
            {
                return number;
            }
            return fallback;
        }
    }

    public void SetDouble(string key, double value)
    {
        lock (_gate)
        {
            _data[key] = value;
            SaveFile();
        }
    }

    private static JsonObject LoadFile(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            if (JsonNode.Parse(text) is JsonObject obj) return obj;
        }
        catch
        {
        }
        return [];
    }

    private void SaveFile()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, _data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
        }
    }
}
