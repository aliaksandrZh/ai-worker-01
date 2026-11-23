using PiAgent.BuildWorker.Docker;
using PiAgent.BuildWorker.Builds;
using PiAgent.BuildWorker.Api;
using System.Text;

namespace PiAgent.BuildWorker.Builds;

/// <summary>
/// Minimal ustar tar writer for docker build contexts (the Docker Engine HTTP API `POST /build` takes an
/// x-tar body). Deterministic: entries are sorted by path and mtimes are pinned to the epoch, so the same
/// pinned build context always tars to identical bytes. Files only (the profile snapshot is a file map);
/// parent directories are emitted as entries for conventional tooling. No new packages — verified in
/// tests by reading the archive back with the BCL `System.Formats.Tar`.
/// </summary>
public static class TarBuilder
{
    public const long FileMode = 420; // 0644 octal

    public static byte[] Create(string rootDirectory)
    {
        using var output = new MemoryStream();
        long written = 0;

        var files = Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(rootDirectory, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        foreach (var directory in files.SelectMany(NeededParentDirectories).Distinct().OrderBy(p => p, StringComparer.Ordinal))
            written += WriteEntry(output, directory, isDirectory: true, content: []);

        foreach (var relative in files)
            written += WriteEntry(output, relative, isDirectory: false, content: File.ReadAllBytes(Path.Combine(rootDirectory, relative)));

        output.Write(new byte[1024]); // end-of-archive marker
        written += 1024;
        if (written % 512 != 0)
            output.Write(new byte[512 - written % 512]);
        return output.ToArray();
    }

    private static IEnumerable<string> NeededParentDirectories(string relative)
    {
        var parts = relative.Split('/');
        for (var depth = 1; depth < parts.Length; depth++)
            yield return string.Join('/', parts.Take(depth));
    }

    private static long WriteEntry(Stream output, string name, bool isDirectory, byte[] content)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name + (isDirectory ? "/" : ""));
        if (nameBytes.Length > 100)
            throw new ArgumentException($"tar entry name too long: {name}");

        var header = new byte[512];
        AppendField(header, 0, nameBytes);
        AppendField(header, 100, EncodeOctal(isDirectory ? Convert.ToInt64("755", 8) : FileMode, 7));
        AppendField(header, 108, EncodeOctal(0, 7)); // uid
        AppendField(header, 116, EncodeOctal(0, 7)); // gid
        AppendField(header, 124, EncodeOctal(isDirectory ? 0 : content.Length, 11));
        AppendField(header, 136, EncodeOctal(0, 11)); // mtime: pinned to the epoch for determinism
        AppendField(header, 148, new byte[8]); // checksum placeholder (spaces)
        header[156] = (byte)(isDirectory ? '5' : '0');
        AppendField(header, 257, "ustar\0"u8.ToArray());
        AppendField(header, 263, "00"u8.ToArray());

        var checksum = header.Sum(field => (long)field) + 256; // POSIX: the chksum field itself counts as 8 spaces (0x20×8)
        // checksum field: 6 octal digits + NUL + space (8 bytes)
        var checksumBytes = EncodeOctal(checksum, 7);
        var checksumField = new byte[8];
        checksumBytes.CopyTo(checksumField, 0);
        checksumField[7] = (byte)' ';
        AppendField(header, 148, checksumField);

        output.Write(header);
        var written = 512L;
        if (!isDirectory)
        {
            output.Write(content);
            var padding = content.Length % 512 == 0 ? 0 : 512 - content.Length % 512;
            output.Write(new byte[padding]);
            written += content.Length + padding;
        }
        return written;
    }

    private static void AppendField(byte[] header, int offset, byte[] field)
    {
        if (offset + field.Length > header.Length)
            throw new InvalidOperationException("tar field overflow");
        field.CopyTo(header, offset);
    }

    private static byte[] EncodeOctal(long value, int width)
    {
        var text = Convert.ToString(value, 8);
        return Encoding.UTF8.GetBytes(text.PadLeft(width - 1, '0') + "\0");
    }
}
