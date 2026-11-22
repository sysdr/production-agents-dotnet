#nullable enable
// UNSUPPORTED: lets C# 15 unions compile on net10.0 for in-process use only.
// System.Text.Json on .NET 10 does not understand unions. Do not serialize these.
namespace System.Runtime.CompilerServices
{
    public interface IUnion { object? Value { get; } }
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
    public sealed class UnionAttribute : Attribute { }
}
