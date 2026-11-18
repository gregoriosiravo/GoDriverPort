namespace LegionGoConverter.Core;

public interface ISessionLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
    string Path { get; }
}
