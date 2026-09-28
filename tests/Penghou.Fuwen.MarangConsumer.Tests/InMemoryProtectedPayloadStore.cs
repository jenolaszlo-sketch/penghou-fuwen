using System.Security.Cryptography;
using Penghou.Fuwen;

namespace Penghou.Fuwen.MarangConsumer.Tests;

internal sealed class InMemoryProtectedPayloadStore : IInferenceProtectedPayloadStore
{
    private readonly Dictionary<string, (string Interaction, string Scope, DescriptorReference Tool, byte[] Bytes, ProtectedPayloadReference Reference)> entries = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public int ReadCount { get; private set; }
    public bool CorruptReads { get; set; }

    public ValueTask<ProtectedPayloadReference> PutAsync(
        InferenceProtectedPayloadWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = request.Content.ToArray();
        var digest = new ContentDigest("sha256", "inference-tool-result/v1",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        lock (gate)
        {
            if (entries.TryGetValue(request.OperationKey, out var existing))
            {
                if (!existing.Bytes.AsSpan().SequenceEqual(bytes) || existing.Interaction != request.InteractionId ||
                    existing.Scope != request.Scope || !existing.Tool.Equals(request.Tool))
                    throw new InvalidOperationException("Protected payload operation key was reused with different content.");
                return ValueTask.FromResult(existing.Reference);
            }

            var reference = new ProtectedPayloadReference(
                "test-store", "payload-" + entries.Count, digest, request.Tool, bytes.LongLength,
                retentionPolicyRevision: "test/1", storageIdentity: "test-identity");
            entries.Add(request.OperationKey, (request.InteractionId, request.Scope, request.Tool, bytes, reference));
            return ValueTask.FromResult(reference);
        }
    }

    public ValueTask<ReadOnlyMemory<byte>?> GetAsync(
        InferenceProtectedPayloadReadRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ReadCount++;
            var found = entries.Values.FirstOrDefault(item => item.Reference.PayloadId == request.Payload.PayloadId);
            if (found.Bytes is null || found.Interaction != request.InteractionId || found.Scope != request.Scope ||
                !found.Tool.Equals(request.Tool))
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
            var bytes = found.Bytes.ToArray();
            if (CorruptReads && bytes.Length > 0)
                bytes[0] ^= 0x01;
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(bytes);
        }
    }

    public bool ContainsText(string text)
    {
        lock (gate)
            return entries.Values.Any(item => System.Text.Encoding.UTF8.GetString(item.Bytes).Contains(text, StringComparison.Ordinal));
    }
}
