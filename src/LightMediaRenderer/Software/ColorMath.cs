#nullable enable
using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace LightMediaRenderer.Software;

// The scalar and SIMD kernels instantiate the same FP32 color equations.
internal interface IColorMath<T> where T : struct, IColorMath<T>
{
    static abstract implicit operator T(float value);
    static abstract T operator +(T left, T right);
    static abstract T operator -(T left, T right);
    static abstract T operator *(T left, T right);
    static abstract T operator /(T left, T right);
    static abstract T Min(T left, T right);
    static abstract T Max(T left, T right);
    static abstract T Pow(T value, float exponent);
    static abstract T Exp(T value);
    static abstract T Log(T value);
    static abstract T SelectGreater(T left, T right, T yes, T no);
}

internal readonly record struct ScalarColor(float Value) : IColorMath<ScalarColor>
{
    public static implicit operator ScalarColor(float value) => new(value);
    public static ScalarColor operator +(ScalarColor a, ScalarColor b) => new(a.Value + b.Value);
    public static ScalarColor operator -(ScalarColor a, ScalarColor b) => new(a.Value - b.Value);
    public static ScalarColor operator *(ScalarColor a, ScalarColor b) => new(a.Value * b.Value);
    public static ScalarColor operator /(ScalarColor a, ScalarColor b) => new(a.Value / b.Value);
    public static ScalarColor Min(ScalarColor a, ScalarColor b) => MathF.Min(a.Value, b.Value);
    public static ScalarColor Max(ScalarColor a, ScalarColor b) => MathF.Max(a.Value, b.Value);
    public static ScalarColor Pow(ScalarColor value, float exponent) => MathF.Pow(value.Value, exponent);
    public static ScalarColor Exp(ScalarColor value) => MathF.Exp(value.Value);
    public static ScalarColor Log(ScalarColor value) => MathF.Log(value.Value);
    public static ScalarColor SelectGreater(ScalarColor a, ScalarColor b, ScalarColor yes, ScalarColor no) =>
        a.Value > b.Value ? yes : no;
}

internal readonly record struct SimdColor(Vector<float> Value) : IColorMath<SimdColor>
{
    public static implicit operator SimdColor(float value) => new(new Vector<float>(value));
    public static SimdColor operator +(SimdColor a, SimdColor b) => new(a.Value + b.Value);
    public static SimdColor operator -(SimdColor a, SimdColor b) => new(a.Value - b.Value);
    public static SimdColor operator *(SimdColor a, SimdColor b) => new(a.Value * b.Value);
    public static SimdColor operator /(SimdColor a, SimdColor b) => new(a.Value / b.Value);
    public static SimdColor Min(SimdColor a, SimdColor b) => new(Vector.Min(a.Value, b.Value));
    public static SimdColor Max(SimdColor a, SimdColor b) => new(Vector.Max(a.Value, b.Value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SimdColor Pow(SimdColor value, float exponent) =>
        new(Vector.Exp(Vector.Log(value.Value) * new Vector<float>(exponent)));

    public static SimdColor Exp(SimdColor value) => new(Vector.Exp(value.Value));
    public static SimdColor Log(SimdColor value) => new(Vector.Log(value.Value));
    public static SimdColor SelectGreater(SimdColor a, SimdColor b, SimdColor yes, SimdColor no) =>
        new(Vector.ConditionalSelect(Vector.GreaterThan(a.Value, b.Value), yes.Value, no.Value));
}
