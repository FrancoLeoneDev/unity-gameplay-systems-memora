public interface ISaveSerializer
{
    string Serialize(SaveData data);
    SaveData Deserialize(string raw);
    SaveMetadata DeserializeMetadataOnly(string raw);
}
