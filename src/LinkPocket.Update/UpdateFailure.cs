namespace LinkPocket.Update;

/// <summary>更新中止（预检拒绝 / 坏包 / 搬运失败——消息面向用户，如实说清怎么修）。</summary>
public sealed class UpdateFailure(string message) : Exception(message);
