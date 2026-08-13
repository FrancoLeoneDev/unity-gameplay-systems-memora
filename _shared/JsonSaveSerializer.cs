using Newtonsoft.Json;

public class JsonSaveSerializer : ISaveSerializer
{
    readonly JsonSerializerSettings jsonSettings;
    readonly JsonSerializer jsonSerializer;

    public JsonSaveSerializer(SaveSettings settings)
    {
        jsonSettings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            Formatting = settings.IndentedFormatting ? Formatting.Indented : Formatting.None,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore
        };

        foreach (var converter in UnityJsonConverters.GetAll())
            jsonSettings.Converters.Add(converter);

        jsonSerializer = JsonSerializer.Create(jsonSettings);
    }

    public JsonSerializer GetSerializer() => jsonSerializer;

    public string Serialize(SaveData data)
    {
        return JsonConvert.SerializeObject(data, jsonSettings);
    }

    public SaveData Deserialize(string raw)
    {
        return JsonConvert.DeserializeObject<SaveData>(raw, jsonSettings);
    }

    public SaveMetadata DeserializeMetadataOnly(string raw)
    {
        var wrapper = JsonConvert.DeserializeAnonymousType(raw,
            new { metadata = new SaveMetadata() }, jsonSettings);
        return wrapper?.metadata ?? new SaveMetadata();
    }
}
