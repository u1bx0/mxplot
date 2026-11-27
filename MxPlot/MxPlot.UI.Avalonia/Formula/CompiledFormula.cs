using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace MxPlot.UI.Avalonia.Formula
{
    /// <summary>An error in a formula's text.</summary>
    internal sealed class FormulaException : Exception
    {
        /// <summary>The zero-based character position the error refers to, or -1 when it has none.</summary>
        public int Position { get; }

        public FormulaException(string message, int position = -1) : base(message) => Position = position;
    }

    /// <summary>
    /// A formula compiled to a delegate. Every value in a formula is a <see cref="double"/>; a comparison
    /// yields 1 or 0, and any non-zero value counts as true.
    /// </summary>
    /// <remarks>
    /// <para>Grammar, lowest precedence first:</para>
    /// <code>
    /// a ? b : c
    /// ||   &amp;&amp;
    /// ==  !=  &lt;  &lt;=  &gt;  &gt;=
    /// +  -
    /// *  /  %
    /// unary -  +  !
    /// number | name | function(args) | ( expression )
    /// reference | reference[ix, iy] | reference[ix, iy, frame] | reference[ix, iy, iz:n [, it:n ...]]
    /// reference(x, y) | reference(x, y, frame) | reference(x, y, iz:n [, ...])
    /// reference.Max([frame]) | reference.Min([frame]) | reference.MaxAll() | reference.MinAll()
    /// </code>
    /// <para>
    /// Names are resolved in this order: a reference, a function (when followed by a parenthesis), a
    /// variable, a constant (<c>pi</c>, <c>tau</c>, <c>e</c>, <c>true</c>, <c>false</c>). Function,
    /// constant and reference-member names ignore case; variable and reference names do not. A reference written alone reads the pixel the formula is evaluated for,
    /// so <c>m1 + m2</c> is the same as <c>m1[ix, iy] + m2[ix, iy]</c>. <c>^</c> is rejected because it
    /// reads as a power but is not one; <c>pow(a, b)</c> is.
    /// </para>
    /// </remarks>
    internal sealed class CompiledFormula
    {
        private readonly Func<FormulaContext, double> _evaluate;

        /// <summary>The formula as written.</summary>
        public string Text { get; }

        /// <summary>The variable names, in the order of <see cref="FormulaContext.V"/>.</summary>
        public IReadOnlyList<string> Variables { get; }

        /// <summary>The reference names, in the order of <see cref="FormulaContext.M"/>.</summary>
        public IReadOnlyList<string> References { get; }

        private CompiledFormula(string text, IReadOnlyList<string> variables, IReadOnlyList<string> references,
            Func<FormulaContext, double> evaluate)
        {
            Text = text;
            Variables = variables;
            References = references;
            _evaluate = evaluate;
        }

        /// <summary>Evaluates the formula for the variable values and references in <paramref name="context"/>.</summary>
        public double Evaluate(FormulaContext context) => _evaluate(context);

        /// <summary>Compiles <paramref name="text"/>.</summary>
        /// <param name="variables">The variable names; a formula reads variable <c>i</c> from <see cref="FormulaContext.V"/>[i].</param>
        /// <param name="references">The reference names; a formula reads reference <c>i</c> from <see cref="FormulaContext.M"/>[i].</param>
        /// <param name="referenceAxes">
        /// For each reference, the names of the axes of its data in their order. A formula names such an axis by its
        /// index variable (<see cref="FormulaEvaluator.AxisIndexLabels(IEnumerable{string})"/>): <c>m1[ix, iy, iz:2]</c>.
        /// The axis is found by name again when the formula runs, in the data it reads then. <see langword="null"/> when no
        /// reference has axes to name.
        /// </param>
        /// <exception cref="FormulaException">The text is not a valid formula.</exception>
        public static CompiledFormula Compile(string text, IReadOnlyList<string> variables, IReadOnlyList<string> references,
            IReadOnlyList<IReadOnlyList<string>>? referenceAxes = null)
        {
            text ??= "";
            var parser = new Parser(text, variables, references, referenceAxes);
            var body = parser.ParseFormula();
            var lambda = Expression.Lambda<Func<FormulaContext, double>>(body, parser.Context);
            return new CompiledFormula(text, variables.ToArray(), references.ToArray(), lambda.Compile());
        }

        /// <summary>The error <paramref name="text"/> would fail to compile with, or <see langword="null"/> when it is valid.</summary>
        public static FormulaException? Check(string text, IReadOnlyList<string> variables, IReadOnlyList<string> references,
            IReadOnlyList<IReadOnlyList<string>>? referenceAxes = null)
        {
            try
            {
                new Parser(text ?? "", variables, references, referenceAxes).ParseFormula();
                return null;
            }
            catch (FormulaException ex) { return ex; }
        }

        private enum TokenKind { Number, Name, Symbol, End }

        private readonly record struct Token(TokenKind Kind, string Text, int Position, double Value = 0);

        /// <summary>A recursive-descent parser that builds the expression tree directly.</summary>
        private sealed class Parser
        {
            private static readonly string[] Symbols =
                ["||", "&&", "==", "!=", "<=", ">=", "<", ">", "+", "-", "*", "/", "%", "!", "?", ":", "(", ")", "[", "]", ",", "."];

            private static readonly MethodInfo AtPixel = RefMethod(nameof(FormulaRef.At), 2);
            private static readonly MethodInfo AtFrame = RefMethod(nameof(FormulaRef.At), 3);
            private static readonly MethodInfo CurrentFrame = RefMethod(nameof(FormulaRef.CurrentFrame), 0);
            private static readonly MethodInfo WithAxis = RefMethod(nameof(FormulaRef.WithAxis), 3);

            private readonly string _text;
            private readonly IReadOnlyList<string> _variables;
            private readonly IReadOnlyList<string> _references;
            private readonly IReadOnlyList<IReadOnlyList<string>>? _referenceAxes;
            private readonly Expression _v;
            private readonly Expression _m;
            private Token _token;
            private int _pos;

            public ParameterExpression Context { get; } = Expression.Parameter(typeof(FormulaContext), "ctx");

            public Parser(string text, IReadOnlyList<string> variables, IReadOnlyList<string> references,
                IReadOnlyList<IReadOnlyList<string>>? referenceAxes)
            {
                _text = text;
                _variables = variables;
                _references = references;
                _referenceAxes = referenceAxes;
                _v = Expression.Field(Context, nameof(FormulaContext.V));
                _m = Expression.Field(Context, nameof(FormulaContext.M));
            }

            public Expression ParseFormula()
            {
                if (string.IsNullOrWhiteSpace(_text)) throw new FormulaException("The formula is empty.", 0);
                for (int i = 0; i < _text.Length; i++)
                {
                    char c = _text[i];
                    if (c == '　') throw new FormulaException($"Full-width space at position {i + 1}.", i);
                    if (c > 0x7E) throw new FormulaException($"Non-ASCII character '{c}' at position {i + 1} (full-width?).", i);
                    if (c == '^') throw new FormulaException("'^' is not a power here. Use pow(a, b).", i);
                }

                Next();
                var body = ParseTernary();
                if (_token.Kind != TokenKind.End) throw Unexpected();
                return body;
            }

            // ── Tokens ────────────────────────────────────────────────────────

            private void Next()
            {
                while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos])) _pos++;
                if (_pos >= _text.Length)
                {
                    _token = new Token(TokenKind.End, "", _text.Length);
                    return;
                }

                int start = _pos;
                char c = _text[_pos];
                if (char.IsDigit(c) || (c == '.' && _pos + 1 < _text.Length && char.IsDigit(_text[_pos + 1])))
                {
                    while (_pos < _text.Length && char.IsDigit(_text[_pos])) _pos++;
                    if (_pos < _text.Length && _text[_pos] == '.')
                    {
                        _pos++;
                        while (_pos < _text.Length && char.IsDigit(_text[_pos])) _pos++;
                    }
                    if (_pos < _text.Length && (_text[_pos] == 'e' || _text[_pos] == 'E'))
                    {
                        int exp = _pos + 1;
                        if (exp < _text.Length && (_text[exp] == '+' || _text[exp] == '-')) exp++;
                        if (exp < _text.Length && char.IsDigit(_text[exp]))
                        {
                            _pos = exp;
                            while (_pos < _text.Length && char.IsDigit(_text[_pos])) _pos++;
                        }
                    }
                    string number = _text[start.._pos];
                    if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                        throw new FormulaException($"'{number}' is not a number.", start);
                    _token = new Token(TokenKind.Number, number, start, value);
                    return;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_')) _pos++;
                    _token = new Token(TokenKind.Name, _text[start.._pos], start);
                    return;
                }

                foreach (var symbol in Symbols)
                {
                    if (string.CompareOrdinal(_text, _pos, symbol, 0, symbol.Length) != 0) continue;
                    _pos += symbol.Length;
                    _token = new Token(TokenKind.Symbol, symbol, start);
                    return;
                }
                throw new FormulaException($"Unexpected '{c}' at position {start + 1}.", start);
            }

            private bool Is(string symbol) => _token.Kind == TokenKind.Symbol && _token.Text == symbol;

            private bool Accept(string symbol)
            {
                if (!Is(symbol)) return false;
                Next();
                return true;
            }

            private void Expect(string symbol)
            {
                if (!Accept(symbol))
                {
                    throw _token.Kind == TokenKind.End
                        ? new FormulaException($"'{symbol}' is missing at the end.", _token.Position)
                        : new FormulaException($"Expected '{symbol}' at position {_token.Position + 1}.", _token.Position);
                }
            }

            private FormulaException Unexpected() => _token.Kind == TokenKind.End
                ? new FormulaException("The formula is incomplete.", _token.Position)
                : new FormulaException($"Unexpected '{_token.Text}' at position {_token.Position + 1}.", _token.Position);

            // ── Grammar ───────────────────────────────────────────────────────

            private Expression ParseTernary()
            {
                var condition = ParseOr();
                if (!Accept("?")) return condition;
                var whenTrue = ParseTernary();
                Expect(":");
                var whenFalse = ParseTernary();
                return Expression.Condition(IsTrue(condition), whenTrue, whenFalse);
            }

            private Expression ParseOr()
            {
                var left = ParseAnd();
                while (Accept("||")) left = FromBool(Expression.OrElse(IsTrue(left), IsTrue(ParseAnd())));
                return left;
            }

            private Expression ParseAnd()
            {
                var left = ParseComparison();
                while (Accept("&&")) left = FromBool(Expression.AndAlso(IsTrue(left), IsTrue(ParseComparison())));
                return left;
            }

            private Expression ParseComparison()
            {
                var left = ParseAdditive();
                while (true)
                {
                    if (Accept("==")) left = FromBool(Expression.Equal(left, ParseAdditive()));
                    else if (Accept("!=")) left = FromBool(Expression.NotEqual(left, ParseAdditive()));
                    else if (Accept("<=")) left = FromBool(Expression.LessThanOrEqual(left, ParseAdditive()));
                    else if (Accept(">=")) left = FromBool(Expression.GreaterThanOrEqual(left, ParseAdditive()));
                    else if (Accept("<")) left = FromBool(Expression.LessThan(left, ParseAdditive()));
                    else if (Accept(">")) left = FromBool(Expression.GreaterThan(left, ParseAdditive()));
                    else return left;
                }
            }

            private Expression ParseAdditive()
            {
                var left = ParseMultiplicative();
                while (true)
                {
                    if (Accept("+")) left = Expression.Add(left, ParseMultiplicative());
                    else if (Accept("-")) left = Expression.Subtract(left, ParseMultiplicative());
                    else return left;
                }
            }

            private Expression ParseMultiplicative()
            {
                var left = ParseUnary();
                while (true)
                {
                    if (Accept("*")) left = Expression.Multiply(left, ParseUnary());
                    else if (Accept("/")) left = Expression.Divide(left, ParseUnary());
                    else if (Accept("%")) left = Expression.Modulo(left, ParseUnary());
                    else return left;
                }
            }

            private Expression ParseUnary()
            {
                if (Accept("-")) return Expression.Negate(ParseUnary());
                if (Accept("+")) return ParseUnary();
                if (Accept("!")) return FromBool(Expression.Equal(ParseUnary(), Expression.Constant(0.0)));
                return ParsePrimary();
            }

            private Expression ParsePrimary()
            {
                if (_token.Kind == TokenKind.Number)
                {
                    var number = Expression.Constant(_token.Value);
                    Next();
                    return number;
                }
                if (Accept("("))
                {
                    var inner = ParseTernary();
                    Expect(")");
                    return inner;
                }
                if (_token.Kind != TokenKind.Name) throw Unexpected();

                var name = _token;
                Next();

                int reference = IndexOf(_references, name.Text);
                if (reference >= 0) return ParseReference(name, reference);

                if (Is("(")) return ParseCall(name);

                int variable = IndexOf(_variables, name.Text);
                if (variable >= 0) return Variable(variable);

                switch (name.Text.ToLowerInvariant())
                {
                    case "pi": return Expression.Constant(Math.PI);
                    case "tau": return Expression.Constant(Math.Tau);
                    case "e": return Expression.Constant(Math.E);
                    case "true": return Expression.Constant(1.0);
                    case "false": return Expression.Constant(0.0);
                }

                if (FormulaFunctions.Exists(name.Text))
                    throw new FormulaException($"'{name.Text}' is a function; write {name.Text}(...).", name.Position);
                throw new FormulaException($"Unknown name '{name.Text}'.", name.Position);
            }

            private Expression ParseCall(Token name)
            {
                if (!FormulaFunctions.Exists(name.Text))
                    throw new FormulaException($"Unknown function '{name.Text}'.", name.Position);

                Expect("(");
                var args = ParseArguments(")");
                var method = FormulaFunctions.Find(name.Text, args.Count)
                    ?? throw new FormulaException(
                        $"{name.Text} takes {FormulaFunctions.DescribeArity(name.Text)}, not {args.Count}.", name.Position);

                // A variadic function is written for two arguments and applied from left to right to more.
                if (args.Count > 2 && FormulaFunctions.IsVariadic(method))
                {
                    Expression folded = Expression.Call(method, args[0], args[1]);
                    for (int i = 2; i < args.Count; i++) folded = Expression.Call(method, folded, args[i]);
                    return folded;
                }
                return Expression.Call(method, args);
            }

            private Expression ParseReference(Token name, int index)
            {
                var reference = Expression.ArrayIndex(_m, Expression.Constant(index));

                if (Accept("["))
                {
                    var (args, axes) = ParseReadArguments("]", name, index);
                    if (args.Count is not (2 or 3))
                        throw new FormulaException($"{name.Text}[...] takes ix, iy, then a frame or axes such as iz:2.", name.Position);
                    if (axes.Count > 0) args.Add(FrameWithAxes(reference, args, axes, name));
                    return Expression.Call(reference, args.Count == 2 ? AtPixel : AtFrame, args);
                }

                if (Accept("."))
                {
                    // Member names ignore case, like function names.
                    string member = _token.Kind == TokenKind.Name ? _token.Text.ToLowerInvariant() : "";
                    int memberPosition = _token.Position;
                    if (member is not ("max" or "min" or "maxall" or "minall"))
                    {
                        throw new FormulaException(
                            $"{name.Text} has Max(), Min(), MaxAll() and MinAll(); read a position as {name.Text}(x, y).", memberPosition);
                    }
                    Next();
                    Expect("(");
                    var args = ParseArguments(")");

                    switch (member)
                    {
                        case "max" or "min":
                            if (args.Count > 1)
                                throw new FormulaException($"{name.Text}.{(member == "max" ? "Max" : "Min")} takes nothing, or a frame.", name.Position);
                            return Expression.Call(reference,
                                RefMethod(member == "max" ? nameof(FormulaRef.Max) : nameof(FormulaRef.Min), args.Count), args);
                        default:
                            if (args.Count != 0)
                                throw new FormulaException($"{name.Text}.{(member == "maxall" ? "MaxAll" : "MinAll")} takes no argument.", name.Position);
                            return Expression.Call(reference,
                                RefMethod(member == "maxall" ? nameof(FormulaRef.MaxAll) : nameof(FormulaRef.MinAll), 0));
                    }
                }

                // A position in physical coordinates, as in a mathematical function of (x, y).
                if (Accept("("))
                {
                    var (position, axes) = ParseReadArguments(")", name, index);
                    if (position.Count is not (2 or 3))
                        throw new FormulaException($"{name.Text}(...) takes x, y, then a frame or axes such as iz:2.", name.Position);
                    if (axes.Count > 0) position.Add(FrameWithAxes(reference, position, axes, name));
                    return Expression.Call(reference, RefMethod(nameof(FormulaRef.AtPosition), position.Count), position);
                }

                // A reference alone reads the pixel being evaluated.
                int ix = IndexOf(_variables, "ix");
                int iy = IndexOf(_variables, "iy");
                if (ix < 0 || iy < 0)
                    throw new FormulaException($"Read {name.Text} as {name.Text}[ix, iy] or {name.Text}(x, y).", name.Position);
                return Expression.Call(reference, AtPixel, Variable(ix), Variable(iy));
            }

            /// <summary>
            /// Parses the arguments of a read of a reference, up to <paramref name="close"/>, which is consumed: the
            /// positions and optionally a frame, then any axes named as <c>iz:2</c>. The value after the colon is an
            /// expression.
            /// </summary>
            private (List<Expression> Positions, List<(string AxisName, Expression Value)> Axes) ParseReadArguments(
                string close, Token reference, int referenceIndex)
            {
                var positions = new List<Expression>();
                var axes = new List<(string AxisName, Expression Value)>();
                IReadOnlyList<string> names = _referenceAxes != null && referenceIndex < _referenceAxes.Count
                    ? _referenceAxes[referenceIndex]
                    : Array.Empty<string>();
                var labels = FormulaEvaluator.AxisIndexLabels(names);

                if (Accept(close)) return (positions, axes);
                do
                {
                    // A name followed by a colon is an axis; a colon after "?" belongs to a conditional, which
                    // never begins an argument with a name and a colon.
                    if (_token.Kind == TokenKind.Name && PeekIsColon())
                    {
                        var axisName = _token;
                        int axis = IndexOf(labels, axisName.Text);
                        if (axis < 0)
                        {
                            throw new FormulaException(labels.Length == 0
                                ? $"{reference.Text} has no axes."
                                : $"{reference.Text} has no axis '{axisName.Text}'. Its axes: {string.Join(", ", labels)}.",
                                axisName.Position);
                        }
                        if (axes.Any(a => a.AxisName == names[axis]))
                            throw new FormulaException($"'{axisName.Text}' is given twice.", axisName.Position);

                        Next();
                        Expect(":");
                        axes.Add((names[axis], ParseTernary()));
                    }
                    else
                    {
                        if (axes.Count > 0) throw new FormulaException("Name the axes after the positions.", _token.Position);
                        positions.Add(ParseTernary());
                    }
                } while (Accept(","));
                Expect(close);
                return (positions, axes);
            }

            /// <summary>The frame of a read: the frame being read, moved along each named axis.</summary>
            private Expression FrameWithAxes(Expression reference, List<Expression> positions,
                List<(string AxisName, Expression Value)> axes, Token referenceName)
            {
                if (positions.Count == 3)
                    throw new FormulaException($"Give {referenceName.Text} a frame number or axes, not both.", referenceName.Position);

                Expression frame = Expression.Call(reference, CurrentFrame);
                foreach (var (axisName, value) in axes)
                    frame = Expression.Call(reference, WithAxis, frame, Expression.Constant(axisName), value);
                return frame;
            }

            private bool PeekIsColon()
            {
                int i = _pos;
                while (i < _text.Length && char.IsWhiteSpace(_text[i])) i++;
                return i < _text.Length && _text[i] == ':';
            }

            /// <summary>Parses a comma-separated list up to <paramref name="close"/>, which is consumed.</summary>
            private List<Expression> ParseArguments(string close)
            {
                var args = new List<Expression>();
                if (Accept(close)) return args;
                do args.Add(ParseTernary());
                while (Accept(","));
                Expect(close);
                return args;
            }

            // ── Building blocks ───────────────────────────────────────────────

            private Expression Variable(int index) => Expression.ArrayIndex(_v, Expression.Constant(index));

            private static Expression IsTrue(Expression value) => Expression.NotEqual(value, Expression.Constant(0.0));

            private static Expression FromBool(Expression condition)
                => Expression.Condition(condition, Expression.Constant(1.0), Expression.Constant(0.0));

            private static int IndexOf(IReadOnlyList<string> names, string name)
            {
                for (int i = 0; i < names.Count; i++)
                    if (names[i] == name) return i;
                return -1;
            }

            private static MethodInfo RefMethod(string name, int argumentCount)
                => typeof(FormulaRef).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .First(m => m.Name == name && m.GetParameters().Length == argumentCount);
        }
    }
}
