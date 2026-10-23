// Game assemblies ship stripped public copies of these attributes, which hide the compiler's own.
namespace System.Runtime.CompilerServices;

[AttributeUsage(AttributeTargets.All, Inherited = false)]
internal sealed class NullableAttribute : Attribute
{
    public readonly byte[] NullableFlags;
    public NullableAttribute(byte value) => NullableFlags = new[] { value };
    public NullableAttribute(byte[] value) => NullableFlags = value;
}

[AttributeUsage(AttributeTargets.All, Inherited = false)]
internal sealed class NullableContextAttribute : Attribute
{
    public readonly byte Flag;
    public NullableContextAttribute(byte value) => Flag = value;
}
