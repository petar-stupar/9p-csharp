using System.Text.Encodings.Web;
using System.Text.Json;
using NineP.Protocol;

namespace NineP.JsonFs;

internal sealed partial class JsonTree
{
    // Internal configuration lets boundary tests exercise the production policy with tiny trees.
    internal long DocumentLimit { get; set; } = MaxDocumentBytes;
    internal int DepthLimit { get; set; } = MaxDepth;

    internal Action Capture()
    {
        List<Action> undo = [];
        Stack<JsonTreeNode> pending = new();
        pending.Push(Root);
        while (pending.TryPop(out JsonTreeNode? node))
        {
            uint version = node.Version;
            if (node is JsonDirectoryNode directory)
            {
                JsonChild[] children = [.. directory.Children];
                undo.Add(() => { directory.Restore(children); directory.Version = version; });
                foreach (JsonChild child in children)
                {
                    pending.Push(child.Node);
                }
            }
            else if (node is JsonScalarNode scalar)
            {
                string text = scalar.Text;
                JsonScalarKind kind = scalar.Kind;
                undo.Add(() => { scalar.Text = text; scalar.Kind = kind; scalar.Version = version; });
            }
        }
        return () => { foreach (Action restore in undo) { restore(); } };
    }

    internal void ValidateGrowth()
    {
        Stack<(JsonDirectoryNode Node, int Depth)> pending = new();
        pending.Push((Root, 1));
        while (pending.TryPop(out var item))
        {
            if (item.Depth > DepthLimit)
            {
                throw new NinePException(NinePError.FromErrno(Errno.ENOSPC));
            }

            foreach (JsonChild child in item.Node.Children)
            {
                if (child.Node is JsonDirectoryNode directory)
                {
                    pending.Push((directory, item.Depth + 1));
                }
            }
        }

        // Count the exact indented write-back representation, including escaped keys and values.
        // Nothing is retained: checking a document does not make a second document-sized buffer.
        using CountingStream stream = new(DocumentLimit);
        using Utf8JsonWriter writer = new(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        Write(writer);
        writer.Flush();
    }

    private sealed class CountingStream(long limit) : Stream
    {
        private long _count;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _count;
        public override long Position { get => _count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length >= limit - _count)
            {
                throw new NinePException(NinePError.FromErrno(Errno.ENOSPC));
            }

            _count += buffer.Length;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
