using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MxPlot.UI.Avalonia.Formula
{
    /// <summary>How a formula function is presented to the user in the formula guide.</summary>
    /// <param name="category">The group the function is listed under.</param>
    /// <param name="signature">How the function is written, e.g. <c>gauss(x, sigma)</c>.</param>
    /// <param name="description">What it gives, or <see langword="null"/> when the name says it all.</param>
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class FormulaDocAttribute(string category, string signature, string? description = null) : Attribute
    {
        public string Category { get; } = category;
        public string Signature { get; } = signature;
        public string? Description { get; } = description;
    }

    /// <summary>
    /// Marks a function of two arguments that a formula may call with any number of them (two or more): the
    /// call is applied from left to right, so <c>sum(a, b, c)</c> is <c>sum(sum(a, b), c)</c>. The function must
    /// give the same result however the arguments are grouped.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class FormulaVariadicAttribute : Attribute;

    /// <summary>A formula function as the guide lists it.</summary>
    internal sealed record FormulaFunctionDoc(string Name, string Category, string Signature, string? Description);

    /// <summary>
    /// The functions a formula can call. Every public static method here whose parameters and return value
    /// are all <see cref="double"/> is a formula function, under its own name with case ignored; overloads
    /// are told apart by their number of arguments. Each carries a <see cref="FormulaDocAttribute"/>, from
    /// which the formula guide is built.
    /// </summary>
    internal static class FormulaFunctions
    {
        private static readonly MethodInfo[] Methods = typeof(FormulaFunctions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(double) && m.GetParameters().All(p => p.ParameterType == typeof(double)))
            .OrderBy(m => m.MetadataToken)
            .ToArray();

        private static readonly Dictionary<string, MethodInfo[]> Table = Methods
            .GroupBy(m => m.Name.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.ToArray());

        /// <summary>The function names, in lower case and sorted.</summary>
        public static IReadOnlyList<string> Names { get; } = Table.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();

        /// <summary>Every function, overloads included, in the order they are declared.</summary>
        public static IReadOnlyList<FormulaFunctionDoc> Docs { get; } = Methods.Select(m =>
        {
            string name = m.Name.ToLowerInvariant();
            var doc = m.GetCustomAttribute<FormulaDocAttribute>();
            return doc != null
                ? new FormulaFunctionDoc(name, doc.Category, doc.Signature, doc.Description)
                : new FormulaFunctionDoc(name, "Other",
                    $"{name}({string.Join(", ", m.GetParameters().Select(p => p.Name))})", null);
        }).ToArray();

        /// <summary>Whether a function is called <paramref name="name"/>.</summary>
        public static bool Exists(string name) => Table.ContainsKey(name.ToLowerInvariant());

        /// <summary>The function <paramref name="name"/> taking <paramref name="argumentCount"/> arguments, or <see langword="null"/>.</summary>
        public static MethodInfo? Find(string name, int argumentCount)
        {
            if (!Table.TryGetValue(name.ToLowerInvariant(), out var overloads)) return null;
            var exact = overloads.FirstOrDefault(m => m.GetParameters().Length == argumentCount);
            return exact ?? (argumentCount > 2 ? overloads.FirstOrDefault(IsVariadic) : null);
        }

        /// <summary>Whether <paramref name="method"/> may be called with more than two arguments (see <see cref="FormulaVariadicAttribute"/>).</summary>
        public static bool IsVariadic(MethodInfo method) => method.GetCustomAttribute<FormulaVariadicAttribute>() != null;

        /// <summary>How many arguments <paramref name="name"/> takes, worded for an error message.</summary>
        public static string DescribeArity(string name)
        {
            var overloads = Table[name.ToLowerInvariant()];
            var counts = overloads.Select(m => m.GetParameters().Length).OrderBy(n => n).ToArray();
            if (overloads.Any(IsVariadic)) return $"{counts.Max()} or more arguments";
            string list = string.Join(" or ", counts);
            return counts is [1] ? "1 argument" : $"{list} arguments";
        }

        private const string Trig = "Trigonometric";
        private const string ExpLog = "Exponential";
        private const string Rounding = "Rounding and range";
        private const string Shapes = "Shapes";
        private const string Utilities = "Utilities";
        private const string RandomNumbers = "Random";

        // ── Elementary ────────────────────────────────────────────────────────

        [FormulaDoc(Trig, "sin(x)")] public static double Sin(double x) => Math.Sin(x);
        [FormulaDoc(Trig, "cos(x)")] public static double Cos(double x) => Math.Cos(x);
        [FormulaDoc(Trig, "tan(x)")] public static double Tan(double x) => Math.Tan(x);
        [FormulaDoc(Trig, "asin(x)")] public static double Asin(double x) => Math.Asin(x);
        [FormulaDoc(Trig, "acos(x)")] public static double Acos(double x) => Math.Acos(x);
        [FormulaDoc(Trig, "atan(x)")] public static double Atan(double x) => Math.Atan(x);

        [FormulaDoc(Trig, "atan2(y, x)", "The angle of the point (x, y), from -pi to pi.")]
        public static double Atan2(double y, double x) => Math.Atan2(y, x);

        [FormulaDoc(Trig, "sinh(x)")] public static double Sinh(double x) => Math.Sinh(x);
        [FormulaDoc(Trig, "cosh(x)")] public static double Cosh(double x) => Math.Cosh(x);
        [FormulaDoc(Trig, "tanh(x)")] public static double Tanh(double x) => Math.Tanh(x);

        [FormulaDoc(ExpLog, "exp(x)")] public static double Exp(double x) => Math.Exp(x);

        [FormulaDoc(ExpLog, "log(x)", "The natural logarithm.")]
        public static double Log(double x) => Math.Log(x);

        [FormulaDoc(ExpLog, "log2(x)")] public static double Log2(double x) => Math.Log2(x);
        [FormulaDoc(ExpLog, "log10(x)")] public static double Log10(double x) => Math.Log10(x);
        [FormulaDoc(ExpLog, "sqrt(x)")] public static double Sqrt(double x) => Math.Sqrt(x);

        [FormulaDoc(ExpLog, "pow(x, y)", "x to the power y. (^ is not a power in a formula.)")]
        public static double Pow(double x, double y) => Math.Pow(x, y);

        [FormulaDoc(ExpLog, "hypot(x, y)", "sqrt(x*x + y*y): the distance of (x, y) from the origin.")]
        public static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

        [FormulaDoc(Rounding, "abs(x)")] public static double Abs(double x) => Math.Abs(x);

        [FormulaDoc(Rounding, "sign(x)", "-1, 0 or 1.")]
        public static double Sign(double x) => double.IsNaN(x) ? double.NaN : Math.Sign(x);

        [FormulaDoc(Rounding, "floor(x)")] public static double Floor(double x) => Math.Floor(x);
        [FormulaDoc(Rounding, "ceil(x)")] public static double Ceil(double x) => Math.Ceiling(x);

        [FormulaDoc(Rounding, "round(x)", "The nearest integer; a half rounds away from zero, so round(2.5) is 3.")]
        public static double Round(double x) => Math.Round(x, MidpointRounding.AwayFromZero);

        [FormulaDoc(Rounding, "min(a, b)")] public static double Min(double a, double b) => Math.Min(a, b);
        [FormulaDoc(Rounding, "max(a, b)")] public static double Max(double a, double b) => Math.Max(a, b);

        [FormulaDoc(Rounding, "clamp(x, min, max)", "x limited to the range min to max.")]
        public static double Clamp(double x, double min, double max) => x < min ? min : x > max ? max : x;

        [FormulaDoc(Rounding, "fract(x)", "The fractional part, x - floor(x).")]
        public static double Fract(double x) => x - Math.Floor(x);

        [FormulaDoc(Rounding, "mod(a, b)", "The remainder with the sign of b, a - b*floor(a/b). (% keeps the sign of a.)")]
        public static double Mod(double a, double b) => a - b * Math.Floor(a / b);

        // ── Shapes ────────────────────────────────────────────────────────────

        [FormulaDoc(Shapes, "rect(x)", "1 for |x| <= 0.5, otherwise 0. A slit of width w: rect(x / w).")]
        public static double Rect(double x) => Math.Abs(x) <= 0.5 ? 1.0 : 0.0;

        [FormulaDoc(Shapes, "circ(r)", "1 for |r| <= 1, otherwise 0. A disc of radius a: circ(r / a).")]
        public static double Circ(double r) => Math.Abs(r) <= 1.0 ? 1.0 : 0.0;

        [FormulaDoc(Shapes, "circ(x, y)", "1 for x*x + y*y <= 1, otherwise 0. A disc of radius a: circ(x / a, y / a); an ellipse: circ(x / a, y / b).")]
        public static double Circ(double x, double y) => x * x + y * y <= 1.0 ? 1.0 : 0.0;

        [FormulaDoc(Shapes, "tri(x)", "The triangle 1 - |x| for |x| < 1, otherwise 0.")]
        public static double Tri(double x) => Math.Max(0.0, 1.0 - Math.Abs(x));

        [FormulaDoc(Shapes, "sinc(x)", "The normalized sinc, sin(pi*x) / (pi*x); 1 at x = 0.")]
        public static double Sinc(double x)
        {
            if (x == 0.0) return 1.0;
            double px = Math.PI * x;
            return Math.Sin(px) / px;
        }

        [FormulaDoc(Shapes, "ring(r, radius, width)", "1 where r is within width/2 of radius, otherwise 0. With the variable r, an annulus: ring(r, 10, 2).")]
        public static double Ring(double r, double radius, double width) => Math.Abs(r - radius) <= width * 0.5 ? 1.0 : 0.0;

        [FormulaDoc(Shapes, "gauss(x, sigma)", "exp(-x*x / (2*sigma*sigma)); 1 at x = 0.")]
        public static double Gauss(double x, double sigma) => Math.Exp(-(x * x) / (2.0 * sigma * sigma));

        [FormulaDoc(Shapes, "gauss2d(x, y, sigma)", "exp(-(x*x + y*y) / (2*sigma*sigma)); 1 at the origin.")]
        public static double Gauss2d(double x, double y, double sigma) => Math.Exp(-(x * x + y * y) / (2.0 * sigma * sigma));

        // ── Utilities ─────────────────────────────────────────────────────────

        [FormulaDoc(Utilities, "step(edge, x)", "0 for x < edge, otherwise 1.")]
        public static double Step(double edge, double x) => x < edge ? 0.0 : 1.0;

        [FormulaDoc(Utilities, "smoothstep(edge0, edge1, x)", "A smooth rise from 0 to 1 as x goes from edge0 to edge1; 0 before, 1 after.")]
        public static double Smoothstep(double edge0, double edge1, double x)
        {
            double t = Clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
            return t * t * (3.0 - 2.0 * t);
        }

        [FormulaDoc(Utilities, "lerp(a, b, t)", "a + (b - a)*t: a at t = 0, b at t = 1.")]
        public static double Lerp(double a, double b, double t) => a + (b - a) * t;

        [FormulaDoc(Utilities, "sum(a, b, ...)", "The sum of two or more values, ignoring NaN; NaN only when every value is NaN. Overlay shifted data: sum(m1(x - 0.5, y), m2).")]
        [FormulaVariadic]
        public static double Sum(double a, double b) => double.IsNaN(a) ? b : double.IsNaN(b) ? a : a + b;

        [FormulaDoc(Utilities, "num(v)", "v, or 0 when v is NaN. A read outside the data is NaN: num(m1[ix + 10, iy]) + m2.")]
        public static double Num(double v) => double.IsNaN(v) ? 0.0 : v;

        [FormulaDoc(Utilities, "num(v, a)", "v, or a when v is NaN.")]
        public static double Num(double v, double a) => double.IsNaN(v) ? a : v;

        // ── Random ────────────────────────────────────────────────────────────

        [FormulaDoc(RandomNumbers, "rand()", "A uniform random number from 0 to 1, new for every pixel.")]
        public static double Rand() => Random.Shared.NextDouble();

        [FormulaDoc(RandomNumbers, "rand(min, max)", "A uniform random number from min to max.")]
        public static double Rand(double min, double max) => min + (max - min) * Random.Shared.NextDouble();

        [FormulaDoc(RandomNumbers, "randn()", "A normal (Gaussian) random number with mean 0 and standard deviation 1.")]
        public static double Randn()
        {
            // Box-Muller; 1 - NextDouble() keeps the logarithm's argument above zero.
            double u1 = 1.0 - Random.Shared.NextDouble();
            double u2 = Random.Shared.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        [FormulaDoc(RandomNumbers, "randn(mean, sigma)", "A normal random number with the given mean and standard deviation.")]
        public static double Randn(double mean, double sigma) => mean + sigma * Randn();

        [FormulaDoc(RandomNumbers, "poisson(lambda)", "A random count with mean lambda (shot noise). poisson(m1) replaces each value by a count around it.")]
        public static double Poisson(double lambda)
        {
            if (!(lambda > 0.0)) return 0.0;
            // Multiplying uniforms underflows for a large mean, where the normal approximation is close.
            if (lambda > 30.0) return Math.Max(0.0, Math.Round(lambda + Math.Sqrt(lambda) * Randn()));

            double limit = Math.Exp(-lambda);
            double product = Random.Shared.NextDouble();
            int count = 0;
            while (product > limit)
            {
                count++;
                product *= Random.Shared.NextDouble();
            }
            return count;
        }

        [FormulaDoc(RandomNumbers, "salt(p)", "1 with probability p, otherwise 0. Isolated bright pixels: m1 + 1000 * salt(0.001).")]
        public static double Salt(double p) => Random.Shared.NextDouble() < p ? 1.0 : 0.0;

        [FormulaDoc(RandomNumbers, "noise(x, y)", "Smooth noise from 0 to 1 with features about one unit across; the same (x, y) always gives the same value.")]
        public static double Noise(double x, double y)
        {
            double fx = Math.Floor(x), fy = Math.Floor(y);
            int ix = (int)fx, iy = (int)fy;
            double tx = x - fx, ty = y - fy;
            tx = tx * tx * (3.0 - 2.0 * tx);
            ty = ty * ty * (3.0 - 2.0 * ty);
            double bottom = Lerp(Lattice(ix, iy), Lattice(ix + 1, iy), tx);
            double top = Lerp(Lattice(ix, iy + 1), Lattice(ix + 1, iy + 1), tx);
            return Lerp(bottom, top, ty);
        }

        /// <summary>A fixed pseudo-random value in [0, 1) for a lattice point.</summary>
        private static double Lattice(int ix, int iy)
        {
            unchecked
            {
                uint h = (uint)ix * 374761393u + (uint)iy * 668265263u;
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return h / 4294967296.0;
            }
        }
    }
}
