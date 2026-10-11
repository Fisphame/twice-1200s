namespace Twice1200.Core.Runtime;

/// <summary>可执行文件的 CPU 架构。</summary>
public enum PeArchitecture
{
    /// <summary>无法识别。</summary>
    Unknown,

    /// <summary>32 位(x86)。</summary>
    X86,

    /// <summary>64 位(x64 / AMD64)。</summary>
    X64,

    /// <summary>ARM64。</summary>
    Arm64,
}

/// <summary>
/// 极简 PE 头解析:只取 CPU 架构与"这是不是一个真正的可执行文件"。
/// <para>
/// 为什么需要它:分发包里的二进制如果架构不对(例如 32 位 exe 配 64 位系统、
/// 或者文件在传输中损坏 / 被杀软替换),错误现象往往是"闪退"或"什么都没发生",
/// 用户完全无从下手。提前校验能给出明确结论。
/// </para>
/// </summary>
public static class PeInspector
{
    private const ushort DosSignature = 0x5A4D;   // "MZ"
    private const uint PeSignature = 0x00004550;  // "PE\0\0"

    /// <summary>读取文件的 PE 架构。</summary>
    /// <returns>无法识别时返回 <see cref="PeArchitecture.Unknown"/>。</returns>
    public static PeArchitecture ReadArchitecture(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 0x40)
            {
                return PeArchitecture.Unknown;
            }

            if (reader.ReadUInt16() != DosSignature)
            {
                return PeArchitecture.Unknown;
            }

            stream.Position = 0x3C;
            var peOffset = reader.ReadUInt32();

            if (peOffset + 6 > stream.Length)
            {
                return PeArchitecture.Unknown;
            }

            stream.Position = peOffset;
            if (reader.ReadUInt32() != PeSignature)
            {
                return PeArchitecture.Unknown;
            }

            return reader.ReadUInt16() switch
            {
                0x014C => PeArchitecture.X86,
                0x8664 => PeArchitecture.X64,
                0xAA64 => PeArchitecture.Arm64,
                _ => PeArchitecture.Unknown,
            };
        }
        catch
        {
            return PeArchitecture.Unknown;
        }
    }

    /// <summary>是否以合法的 MZ/PE 头开头。用于判断文件是否为损坏或被替换。</summary>
    public static bool LooksLikeExecutable(string path)
        => ReadArchitecture(path) != PeArchitecture.Unknown;

    /// <summary>人可读的架构描述。</summary>
    public static string Describe(PeArchitecture architecture) => architecture switch
    {
        PeArchitecture.X86 => "32 位 (x86)",
        PeArchitecture.X64 => "64 位 (x64)",
        PeArchitecture.Arm64 => "ARM64",
        _ => "无法识别",
    };
}
