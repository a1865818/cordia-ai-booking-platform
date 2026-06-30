namespace Cordia.Domain.Interfaces;

/// <summary>
/// Stricter-access sink for model prompts/outputs (§25.3, §57 implementation map).
///
/// <para>Model prompts/outputs are NOT ordinary logs: they carry stricter access and shorter
/// retention and MUST NOT be co-mingled with app logs (§25.3, Inv-12). Implementations route
/// these records to a separate, tagged store that is excluded from the ordinary app sink.</para>
/// </summary>
public interface IModelTraceSink
{
    /// <summary>
    /// Records a model-trace entry of the given <paramref name="kind"/> (e.g. <c>"prompt"</c>,
    /// <c>"completion"</c>) with its <paramref name="content"/> and optional structured attributes.
    /// </summary>
    void Record(string kind, string content, IReadOnlyDictionary<string, string>? attributes = null);
}
