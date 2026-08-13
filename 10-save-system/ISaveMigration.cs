public interface ISaveMigration
{
    int FromVersion { get; }
    int ToVersion { get; }
    void Migrate(SaveData data);
}
