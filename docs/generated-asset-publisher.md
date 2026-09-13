# Generated-asset publisher contract

`IBaizeGeneratedAssetPublisher` is a trusted host boundary, not a remote-fetch
utility supplied by Fuwen. It turns an ordered Baize `GeneratedAsset` batch into
durable `ArtifactPublicationReceipt` values.

For one `InferenceExecutionRequest`, an implementation must:

1. use `request.Invocation.OperationKey` as the idempotency identity for the
   complete ordered batch;
2. return one receipt per input asset, in the same order;
3. retrieve or read each asset through host-controlled credentials and policy;
4. compute `ContentDigest` and `ByteLength` from the exact immutable bytes that
   were durably stored, rather than trusting provider metadata;
5. bind every artifact to the supplied nominal artifact descriptor;
6. make a repeated equivalent call return the same artifact identities with a
   replay disposition, without duplicating bytes or logical publications;
7. publish atomically, or persist enough per-item progress to resume a partial
   batch safely under the same operation key.

The Fuwen Baize adapter verifies receipt count, operation identity, descriptor,
and ordering as returned. It deliberately cannot verify remote bytes itself:
Fuwen receives no credentials or dereferenced content. A publisher exception or
invalid receipt becomes `PublicationRejected` with
`MayHaveCommittedEffect = true`, allowing durable orchestration to preserve the
ambiguity and retry the same idempotent operation safely.

Publishers should reject a repeated operation key if the requested asset batch
is not equivalent to the original request. Retention, encryption, storage
location, malware/content checks, and access control remain host policy.
