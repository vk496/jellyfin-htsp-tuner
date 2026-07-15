using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace HtspTuner.Htsp;

/// <summary>
/// Field type tags of the htsmsg binary encoding (tvheadend <c>htsmsg.h</c>).
/// </summary>
/// <remarks>
/// <c>Dbl</c> (6) exists in the documentation but is rejected by the binary v1 wire format:
/// sending one makes Tvheadend close the connection. It is deliberately absent here.
/// </remarks>
internal enum HtspFieldType : byte
{
    /// <summary>A nested map.</summary>
    Map = 1,

    /// <summary>A signed 64-bit integer, little-endian, minimal length.</summary>
    Int = 2,

    /// <summary>A UTF-8 string.</summary>
    Str = 3,

    /// <summary>Opaque binary data.</summary>
    Bin = 4,

    /// <summary>A list; elements carry empty names.</summary>
    List = 5,

    /// <summary>A boolean. False is encoded with zero-length data.</summary>
    Bool = 7,
}

/// <summary>
/// An htsmsg: an ordered sequence of named fields.
/// </summary>
/// <remarks>
/// This is deliberately <em>not</em> a <see cref="Dictionary{TKey,TValue}"/>. Tvheadend can legitimately
/// emit the same field name twice in one map — <c>subscriptionStart</c> carries one top-level
/// <c>meta</c> field per component that has global headers, so an H264+AAC service emits <c>meta</c>
/// twice. A dictionary-backed decoder throws on the duplicate. Lookups return the first match;
/// <see cref="GetAll"/> returns every match.
/// </remarks>
internal sealed class HtspMessage
{
    private readonly List<KeyValuePair<string, object>> _fields = new();

    /// <summary>Gets the message method, for server-pushed events. Null on responses.</summary>
    public string? Method => GetString("method");

    /// <summary>
    /// Gets the sequence number, for responses. Null on server-pushed events.
    /// </summary>
    /// <remarks>
    /// This is the only reliable way to tell a response from an async event: responses carry
    /// <c>seq</c> and no <c>method</c>; events carry <c>method</c> and no <c>seq</c>.
    /// </remarks>
    public long? Seq => TryGet("seq", out long v) ? v : null;

    /// <summary>Gets the error string Tvheadend returned, if the request failed.</summary>
    public string? Error => GetString("error");

    /// <summary>Gets a value indicating whether the server denied access.</summary>
    public bool NoAccess => GetInt("noaccess") != 0;

    /// <summary>Gets the fields, in wire order.</summary>
    public IReadOnlyList<KeyValuePair<string, object>> Fields => _fields;

    /// <summary>Adds a field. Does not overwrite an existing field of the same name.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="value">The field value.</param>
    /// <returns>This message, for chaining.</returns>
    public HtspMessage Add(string name, object value)
    {
        _fields.Add(new KeyValuePair<string, object>(name, value));
        return this;
    }

    /// <summary>Adds a field only when <paramref name="value"/> is not null or empty.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="value">The field value.</param>
    /// <returns>This message, for chaining.</returns>
    public HtspMessage AddIfPresent(string name, string? value)
        => string.IsNullOrEmpty(value) ? this : Add(name, value);

    /// <summary>Gets every value stored under <paramref name="name"/>.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>The matching values, in wire order.</returns>
    public IEnumerable<object> GetAll(string name)
    {
        foreach (var f in _fields)
        {
            if (string.Equals(f.Key, name, StringComparison.Ordinal))
            {
                yield return f.Value;
            }
        }
    }

    /// <summary>Looks up the first field named <paramref name="name"/> with the requested type.</summary>
    /// <typeparam name="T">The expected value type.</typeparam>
    /// <param name="name">The field name.</param>
    /// <param name="value">The value, when found.</param>
    /// <returns><c>true</c> when a field of that name and type exists.</returns>
    public bool TryGet<T>(string name, [MaybeNullWhen(false)] out T value)
    {
        foreach (var f in _fields)
        {
            if (string.Equals(f.Key, name, StringComparison.Ordinal) && f.Value is T typed)
            {
                value = typed;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>Gets a string field, or null.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>The value, or null.</returns>
    public string? GetString(string name) => TryGet(name, out string? s) ? s : null;

    /// <summary>Gets an integer field, or <paramref name="fallback"/>.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="fallback">Returned when the field is absent.</param>
    /// <returns>The value, or <paramref name="fallback"/>.</returns>
    public long GetInt(string name, long fallback = 0)
        => TryGet(name, out long v) ? v : fallback;

    /// <summary>Gets an integer field, or null when absent.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>The value, or null.</returns>
    public long? GetIntOrNull(string name) => TryGet(name, out long v) ? v : null;

    /// <summary>Gets a boolean field. Tvheadend sends these as 0/1 integers or as real booleans.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>The value.</returns>
    public bool GetBool(string name)
        => TryGet(name, out bool b) ? b : GetInt(name) != 0;

    /// <summary>Gets a binary field, or null.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>The value, or null.</returns>
    public byte[]? GetBin(string name) => TryGet(name, out byte[]? b) ? b : null;

    /// <summary>Gets a nested map, or null.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>The value, or null.</returns>
    public HtspMessage? GetMap(string name) => TryGet(name, out HtspMessage? m) ? m : null;

    /// <summary>Gets a list of maps. Returns empty when the field is absent.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>The nested maps.</returns>
    public IReadOnlyList<HtspMessage> GetMapList(string name)
        => TryGet(name, out List<object>? l)
            ? l!.OfType<HtspMessage>().ToList()
            : Array.Empty<HtspMessage>();

    /// <summary>Gets a list of integers. Returns empty when the field is absent.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>The integers.</returns>
    public IReadOnlyList<long> GetIntList(string name)
        => TryGet(name, out List<object>? l)
            ? l!.OfType<long>().ToList()
            : Array.Empty<long>();

    /// <inheritdoc/>
    public override string ToString()
    {
        var parts = _fields.Select(f => string.Create(
            CultureInfo.InvariantCulture,
            $"{f.Key}={Describe(f.Value)}"));
        return string.Join(", ", parts);

        static string Describe(object v) => v switch
        {
            byte[] b => $"<{b.Length} bytes>",
            List<object> l => $"[{l.Count}]",
            HtspMessage m => $"{{{m.Fields.Count}}}",
            _ => v.ToString() ?? string.Empty,
        };
    }
}
