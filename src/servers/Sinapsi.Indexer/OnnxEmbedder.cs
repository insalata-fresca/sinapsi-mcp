using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Sinapsi.Indexer;

/// <summary>
/// all-MiniLM-L6-v2 embedder, entirely in C#: BERT WordPiece tokenize
/// (Microsoft.ML.Tokenizers) → ONNX Runtime inference → mean-pool over tokens
/// (attention-mask weighted) → L2-normalise → 384-dim. The transformer ONNX
/// outputs token embeddings (last_hidden_state); pooling + norm are done here,
/// per the model card. Inputs/outputs are bound by the model's own metadata
/// names (robust to export variations); token_type_ids fed as zeros if required.
/// Model + vocab paths are env-driven (EMBED_MODEL_PATH / EMBED_VOCAB_PATH);
/// bundle them in the image or mount them at runtime.
/// Thread-safe for concurrent <see cref="Embed"/> (InferenceSession.Run is).
/// </summary>
public sealed class OnnxEmbedder : IEmbedder, IDisposable
{
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tok;
    private readonly ILogger _log;
    private readonly string[] _inputNames;
    private readonly string _outputName;
    private readonly int _maxTokens;

    public int Dim { get; }

    public OnnxEmbedder(ILogger<OnnxEmbedder> log)
    {
        _log = log;
        var modelPath = Env("EMBED_MODEL_PATH", "/opt/models/all-MiniLM-L6-v2/model.onnx");
        var vocabPath = Env("EMBED_VOCAB_PATH", "/opt/models/all-MiniLM-L6-v2/vocab.txt");
        _maxTokens = int.TryParse(Environment.GetEnvironmentVariable("EMBED_MAX_TOKENS"), out var m) ? m : 256;
        Dim = int.TryParse(Environment.GetEnvironmentVariable("EMBED_DIM"), out var d) ? d : 384;

        _tok = BertTokenizer.Create(vocabPath);

        // CPU containment. ONNX Runtime busy-SPINS its thread pool between ops —
        // idle threads burn CPU instead of sleeping, which (with an inline
        // backfill) can peg a shared host. Disable spinning so idle threads
        // sleep; a container CPU cgroup cap bounds how many cores a busy
        // inference may use, and the background EmbedLoop throttles the rate — so
        // ONNX stays free to parallelise up to the cap without the idle-spin waste.
        //
        // NOTE: `SessionOptions` MUST be fully-qualified. This is a
        // `Microsoft.NET.Sdk.Web` project with ImplicitUsings, so the global
        // `Microsoft.AspNetCore.Builder` using introduces a *second*
        // `SessionOptions` type — an unqualified `new SessionOptions()` is
        // CS0104 "ambiguous reference".
        var so = new Microsoft.ML.OnnxRuntime.SessionOptions();
        so.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        so.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        _session = new InferenceSession(modelPath, so);
        _inputNames = _session.InputMetadata.Keys.ToArray();
        _outputName = _session.OutputMetadata.Keys.First();
        _log.LogInformation("OnnxEmbedder ready: inputs=[{in}] output={out} dim={dim}",
            string.Join(",", _inputNames), _outputName, Dim);
    }

    private static string Env(string k, string dflt) =>
        Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : dflt;

    public float[] Embed(string text)
    {
        var encoded = _tok.EncodeToIds(text ?? "");
        var ids = encoded.Count > _maxTokens ? encoded.Take(_maxTokens).ToList() : encoded;
        var len = Math.Max(1, ids.Count);
        var inputIds = new long[len];
        var mask = new long[len];
        var types = new long[len];
        for (var i = 0; i < ids.Count; i++) { inputIds[i] = ids[i]; mask[i] = 1; types[i] = 0; }

        var dims = new[] { 1, len };
        var feeds = new List<NamedOnnxValue>();
        foreach (var name in _inputNames)
        {
            var arr = name.Contains("attention", StringComparison.OrdinalIgnoreCase) ? mask
                    : name.Contains("type", StringComparison.OrdinalIgnoreCase) ? types
                    : inputIds; // input_ids / ids
            feeds.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(arr.AsMemory(), dims)));
        }

        using var results = _session.Run(feeds);
        var outT = results.First(r => r.Name == _outputName).AsTensor<float>(); // [1, len, dim]

        // mean-pool over tokens (mask-weighted) + L2-normalise.
        var pooled = new float[Dim];
        long masked = 0;
        for (var t = 0; t < len; t++)
        {
            if (mask[t] == 0) continue;
            masked++;
            for (var k = 0; k < Dim; k++) pooled[k] += outT[0, t, k];
        }
        if (masked > 0) for (var k = 0; k < Dim; k++) pooled[k] /= masked;

        double norm = 0;
        for (var k = 0; k < Dim; k++) norm += pooled[k] * (double)pooled[k];
        norm = Math.Sqrt(norm);
        if (norm > 0) for (var k = 0; k < Dim; k++) pooled[k] = (float)(pooled[k] / norm);
        return pooled;
    }

    public void Dispose() => _session.Dispose();
}
