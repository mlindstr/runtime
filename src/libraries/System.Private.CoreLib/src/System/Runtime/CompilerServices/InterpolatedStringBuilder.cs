// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;

namespace System.Runtime.CompilerServices
{
    /// <summary>Provides a builder used by the language compiler to process interpolated strings into <see cref="string"/> instances.</summary>
    public ref struct InterpolatedStringBuilder
    {
        // Implementation note:
        // As this type lives in CompilerServices and is only intended to be targeted by the compiler,
        // public APIs eschew argument validation logic in a variety of places, e.g. allowing a null input
        // when one isn't expected to produce a NullReferenceException rather than an ArgumentNullException.

        /// <summary>Expected average length of formatted data used for an individual hole.</summary>
        /// <remarks>
        /// This is inherited from string.Format, and could be changed based on further data.
        /// string.Format actually uses `format.Length + args.Length * 8`, but format.Length
        /// includes the holes themselves, e.g. "{0}", and since it's rare to have double-digit
        /// numbers of holes, we bump the 8 up to 11 to account for the three extra characters in "{d}",
        /// since the compiler-provided base length won't include the equivalent character count.
        /// </remarks>
        private const int GuessedLengthPerHole = 11;
        /// <summary>Minimum size array to rent from the pool.</summary>
        /// <remarks>Same as stack-allocation size used today by string.Format.</remarks>
        private const int MinimumArrayPoolLength = 256;

        /// <summary>Optional provider to pass to IFormattable.ToString or ISpanFormattable.TryFormat calls.</summary>
        private readonly IFormatProvider? _provider;
        /// <summary>Optional custom formatter derived from the <see cref="_provider"/>.</summary>
        private readonly object? _customFormatter;
        /// <summary>Array rented from the array pool and used to back <see cref="_chars"/>.</summary>
        private char[]? _arrayToReturnToPool;
        /// <summary>The span to write into.</summary>
        private Span<char> _chars;
        /// <summary>Position at which to write the next character.</summary>
        private int _pos;

        /// <summary>Initializes the builder.</summary>
        /// <param name="initialCapacity">Approximated capacity required to support the interpolated string.  The final size may be smaller or larger.</param>
        private InterpolatedStringBuilder(int initialCapacity)
        {
            _provider = null;
            _customFormatter = null;
            _chars = _arrayToReturnToPool = ArrayPool<char>.Shared.Rent(initialCapacity);
            _pos = 0;
        }

        /// <summary>Initializes the builder.</summary>
        /// <param name="initialCapacity">Approximated capacity required to support the interpolated string.  The final size may be smaller or larger.</param>
        /// <param name="provider">An object that supplies culture-specific formatting information.</param>
        private InterpolatedStringBuilder(int initialCapacity, IFormatProvider? provider)
        {
            _provider = provider;
            _customFormatter = provider?.GetFormat(typeof(ICustomFormatter));
            _chars = _arrayToReturnToPool = ArrayPool<char>.Shared.Rent(initialCapacity);
            _pos = 0;
        }

        /// <summary>Initializes the builder.</summary>
        /// <param name="scratchBuffer">A buffer temporarily transferred to the builder for use as part of its formatting.  Contents may be overwritten.</param>
        private InterpolatedStringBuilder(Span<char> scratchBuffer)
        {
            _provider = null;
            _customFormatter = null;
            _arrayToReturnToPool = null;
            _chars = scratchBuffer;
            _pos = 0;
        }

        /// <summary>Initializes the builder.</summary>
        /// <param name="scratchBuffer">A buffer temporarily transferred to the builder for use as part of its formatting.  Contents may be overwritten.</param>
        /// <param name="provider">An object that supplies culture-specific formatting information.</param>
        private InterpolatedStringBuilder(Span<char> scratchBuffer, IFormatProvider? provider)
        {
            _provider = provider;
            _customFormatter = provider?.GetFormat(typeof(ICustomFormatter));
            _arrayToReturnToPool = null;
            _chars = scratchBuffer;
            _pos = 0;
        }

        /// <summary>Creates a builder used to translate an interpolated string into a <see cref="string"/>.</summary>
        /// <param name="literalLength">The number of constant characters outside of holes in the interpolated string.</param>
        /// <param name="formattedCount">The number of holes in the interpolated string.</param>
        /// <remarks>This is intended to be called only by compiler-generated code. Arguments are not validated as they'd otherwise be for members intended to be used directly.</remarks>
        public static InterpolatedStringBuilder Create(int literalLength, int formattedCount) =>
            new InterpolatedStringBuilder(GetDefaultLength(literalLength, formattedCount));

        /// <summary>Creates a builder used to translate an interpolated string into a <see cref="string"/>.</summary>
        /// <param name="literalLength">The number of constant characters outside of holes in the interpolated string.</param>
        /// <param name="formattedCount">The number of holes in the interpolated string.</param>
        /// <param name="provider">An object that supplies culture-specific formatting information.</param>
        /// <remarks>This is intended to be called only by compiler-generated code. Arguments are not validated as they'd otherwise be for members intended to be used directly.</remarks>
        public static InterpolatedStringBuilder Create(int literalLength, int formattedCount, IFormatProvider? provider) =>
            new InterpolatedStringBuilder(GetDefaultLength(literalLength, formattedCount), provider);

        /// <summary>Creates a builder used to translate an interpolated string into a <see cref="string"/>.</summary>
        /// <param name="literalLength">The number of constant characters outside of holes in the interpolated string.</param>
        /// <param name="formattedCount">The number of holes in the interpolated string.</param>
        /// <param name="scratchBuffer">A buffer temporarily transferred to the builder for use as part of its formatting.  Contents may be overwritten.</param>
        /// <remarks>This is intended to be called only by compiler-generated code. Arguments are not validated as they'd otherwise be for members intended to be used directly.</remarks>
        public static InterpolatedStringBuilder Create(int literalLength, int formattedCount, Span<char> scratchBuffer) =>
            new InterpolatedStringBuilder(scratchBuffer);

        /// <summary>Creates a builder used to translate an interpolated string into a <see cref="string"/>.</summary>
        /// <param name="literalLength">The number of constant characters outside of holes in the interpolated string.</param>
        /// <param name="formattedCount">The number of holes in the interpolated string.</param>
        /// <param name="provider">An object that supplies culture-specific formatting information.</param>
        /// <param name="scratchBuffer">A buffer temporarily transferred to the builder for use as part of its formatting.  Contents may be overwritten.</param>
        /// <remarks>This is intended to be called only by compiler-generated code. Arguments are not validated as they'd otherwise be for members intended to be used directly.</remarks>
        public static InterpolatedStringBuilder Create(int literalLength, int formattedCount, IFormatProvider? provider, Span<char> scratchBuffer) =>
            new InterpolatedStringBuilder(scratchBuffer, provider);

        /// <summary>Derives a default length with which to seed the builder.</summary>
        /// <param name="literalLength">The number of constant characters outside of holes in the interpolated string.</param>
        /// <param name="formattedCount">The number of holes in the interpolated string.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetDefaultLength(int literalLength, int formattedCount) =>
            Math.Max(MinimumArrayPoolLength, literalLength + (formattedCount * GuessedLengthPerHole));

        /// <summary>Gets the built <see cref="string"/>.</summary>
        /// <returns>The built string.</returns>
        public override string ToString() => _chars.Slice(0, _pos).ToString();

        /// <summary>Gets the built <see cref="string"/> and clears the builder.</summary>
        /// <returns>The built string.</returns>
        /// <remarks>
        /// This releases any resources used by the builder. The method should be invoked only
        /// once and as the last thing performed on the builder. Subsequent use is erroneous, ill-defined,
        /// and may destabilize the process, as may using any other copies of the builder after ToStringAndClear
        /// is called on any one of them.
        /// </remarks>
        public string ToStringAndClear()
        {
            string result = _chars.Slice(0, _pos).ToString();

            char[]? toReturn = _arrayToReturnToPool;
            this = default; // defensive clear
            if (toReturn is not null)
            {
                ArrayPool<char>.Shared.Return(toReturn);
            }

            return result;
        }

        /// <summary>Writes the specified string to the builder.</summary>
        /// <param name="value">The string to write.</param>
        public void AppendLiteral(string value)
        {
            if (value.TryCopyTo(_chars.Slice(_pos)))
            {
                _pos += value.Length;
            }
            else
            {
                GrowThenCopyString(value);
            }
        }

        #region AppendFormatted
        // Design note:
        // The compiler requires a AppendFormatted overload for anything that might be within a hole;
        // if it can't find an appropriate overload, for builders in general it'll simply fail to compile.
        // (For target-typing to string where it uses InterpolatedStringBuilder implicitly, it'll instead fall back to
        // its other mechanisms, e.g. using string.Format.  This fallback has the benefit that if we miss a case,
        // interpolated strings will still work, but it has the downside that a developer generally won't know
        // if the fallback is happening and they're paying more.)
        //
        // At a minimum, then, we would need an overload that accepts:
        //     (object value, int alignment = 0, string? format = null)
        // Such an overload would provide the same expressiveness as string.Format.  However, this has several
        // shortcomings:
        // - Every value type in a hole would be boxed.
        // - ReadOnlySpan<char> could not be used in holes.
        // - Every AppendFormatted call would have three arguments at the call site, bloating the IL further.
        // - Every invocation would be more expensive, due to lack of specialization, every call needing to account
        //   for alignment and format, etc.
        //
        // To address that, we could just have overloads for T and ReadOnlySpan<char>:
        //     (T)
        //     (T, int alignment)
        //     (T, string? format)
        //     (T, int alignment, string? format)
        //     (ReadOnlySpan<char>)
        //     (ReadOnlySpan<char>, int alignment)
        //     (ReadOnlySpan<char>, string? format)
        //     (ReadOnlySpan<char>, int alignment, string? format)
        // but this also has shortcomings:
        // - Some expressions that would have worked with an object overload will now force a fallback to string.Format
        //   (or fail to compile if the builder is used in places where the fallback isn't provided), because the compiler
        //   can't always target type to T, e.g. `b switch { true => 1, false => null }` where `b` is a bool can successfully
        //   be passed as an argument of type `object` but not of type `T`.
        // - Reference types get no benefit from going through the generic code paths, and actually incur some overheads
        //   from doing so.
        // - Nullable value types also pay a heavy price, in particular around interface checks that would generally evaporate
        //   at compile time for value types but don't (currently) if the Nullable<T> goes through the same code paths
        //   (see https://github.com/dotnet/runtime/issues/50915).
        //
        // We could try to take a more elaborate approach for InterpolatedStringBuilder, since it is the most common builder
        // and we want to minimize overheads both at runtime and in IL size, e.g. have a complete set of overloads for each of:
        //     (T, ...) where T : struct
        //     (T?, ...) where T : struct
        //     (object, ...)
        //     (ReadOnlySpan<char>, ...)
        //     (string, ...)
        // but this also has shortcomings, most importantly:
        // - If you have an unconstrained T that happens to be a value type, it'll now end up getting boxed to use the object overload.
        //   This also necessitates the T? overload, since nullable value types don't meet a T : struct constraint, so without those
        //   they'd all map to the object overloads as well.
        // - Any reference type with an implicit cast to ROS<char> will fail to compile due to ambiguities between the overloads. string
        //   is one such type, hence needing dedicated overloads for it that can be bound to more tightly.
        //
        // A middle ground we've settled on, which is likely to be the right approach for most other builders as well, would be the set:
        //     (T, ...) with no constraint
        //     (ReadOnlySpan<char>) and (ReadOnlySpan<char>, int)
        //     (object, int alignment = 0, string? format = null)
        //     (string) and (string, int)
        // This would address most of the concerns, at the expense of:
        // - Most reference types going through the generic code paths and so being a bit more expensive.
        // - Nullable types being more expensive until https://github.com/dotnet/runtime/issues/50915 is addressed.
        //   We could choose to add a T? where T : struct set of overloads if necessary.
        // Strings don't require their own overloads here, but as they're expected to be very common and as we can
        // optimize them in several ways (can copy the contents directly, don't need to do any interface checks, don't
        // need to pay the shared generic overheads, etc.) we can add overloads specifically to optimize for them.
        //
        // Hole values are formatted according to the following policy:
        // 1. If an IFormatProvider was supplied and it provides an ICustomFormatter, use ICustomFormatter.Format (even if the value is null).
        // 2. If the type implements ISpanFormattable, use ISpanFormattable.TryFormat.
        // 3. If the type implements IFormattable, use IFormattable.ToString.
        // 4. Otherwise, use object.ToString.
        // This matches the behavior of string.Format, StringBuilder.AppendFormat, etc.  The only overloads for which this doesn't
        // apply is ReadOnlySpan<char>, which isn't supported by either string.Format nor StringBuilder.AppendFormat, but more
        // importantly which can't be boxed to be passed to ICustomFormatter.Format.

        #region AppendFormatted T
        /// <summary>Writes the specified value to the builder.</summary>
        /// <param name="value">The value to write.</param>
        public void AppendFormatted<T>(T value)
        {
            // This method could delegate to AppendFormatted with a null format, but explicitly passing
            // default as the format to TryFormat helps to improve code quality in some cases when TryFormat is inlined,
            // e.g. for Int32 it enables the JIT to eliminate code in the inlined method based on a length check on the format.

            // If there's a custom formatter, always use it.
            if (_customFormatter is not null)
            {
                AppendCustomFormatter(value, format: null);
                return;
            }

            // Check first for IFormattable, even though we'll prefer to use ISpanFormattable, as the latter
            // requires the former.  For value types, it won't matter as the type checks devolve into
            // JIT-time constants.  For reference types, they're more likely to implement IFormattable
            // than they are to implement ISpanFormattable: if they don't implement either, we save an
            // interface check over first checking for ISpanFormattable and then for IFormattable, and
            // if it only implements IFormattable, we come out even: only if it implements both do we
            // end up paying for an extra interface check.
            string? s;
            if (value is IFormattable)
            {
                // If the value can format itself directly into our buffer, do so.
                if (value is ISpanFormattable)
                {
                    int charsWritten;
                    while (!((ISpanFormattable)value).TryFormat(_chars.Slice(_pos), out charsWritten, default, _provider)) // constrained call avoiding boxing for value types
                    {
                        Grow();
                    }

                    _pos += charsWritten;
                    return;
                }

                s = ((IFormattable)value).ToString(format: null, _provider); // constrained call avoiding boxing for value types
            }
            else
            {
                s = value?.ToString();
            }

            if (s is not null)
            {
                AppendLiteral(s);
            }
        }
        /// <summary>Writes the specified value to the builder.</summary>
        /// <param name="value">The value to write.</param>
        /// <param name="format">The format string.</param>
        public void AppendFormatted<T>(T value, string? format)
        {
            // If there's a custom formatter, always use it.
            if (_customFormatter is not null)
            {
                AppendCustomFormatter(value, format);
                return;
            }

            // Check first for IFormattable, even though we'll prefer to use ISpanFormattable, as the latter
            // requires the former.  For value types, it won't matter as the type checks devolve into
            // JIT-time constants.  For reference types, they're more likely to implement IFormattable
            // than they are to implement ISpanFormattable: if they don't implement either, we save an
            // interface check over first checking for ISpanFormattable and then for IFormattable, and
            // if it only implements IFormattable, we come out even: only if it implements both do we
            // end up paying for an extra interface check.
            string? s;
            if (value is IFormattable)
            {
                // If the value can format itself directly into our buffer, do so.
                if (value is ISpanFormattable)
                {
                    int charsWritten;
                    while (!((ISpanFormattable)value).TryFormat(_chars.Slice(_pos), out charsWritten, format, _provider)) // constrained call avoiding boxing for value types
                    {
                        Grow();
                    }

                    _pos += charsWritten;
                    return;
                }

                s = ((IFormattable)value).ToString(format, _provider); // constrained call avoiding boxing for value types
            }
            else
            {
                s = value?.ToString();
            }

            if (s is not null)
            {
                AppendLiteral(s);
            }
        }

        /// <summary>Writes the specified value to the builder.</summary>
        /// <param name="value">The value to write.</param>
        /// <param name="alignment">Minimum number of characters that should be written for this value.  If the value is negative, it indicates left-aligned and the required minimum is the absolute value.</param>
        public void AppendFormatted<T>(T value, int alignment)
        {
            int startingPos = _pos;
            AppendFormatted(value);
            if (alignment != 0)
            {
                AppendOrInsertAlignmentIfNeeded(startingPos, alignment);
            }
        }

        /// <summary>Writes the specified value to the builder.</summary>
        /// <param name="value">The value to write.</param>
        /// <param name="format">The format string.</param>
        /// <param name="alignment">Minimum number of characters that should be written for this value.  If the value is negative, it indicates left-aligned and the required minimum is the absolute value.</param>
        public void AppendFormatted<T>(T value, int alignment, string? format)
        {
            int startingPos = _pos;
            AppendFormatted(value, format);
            if (alignment != 0)
            {
                AppendOrInsertAlignmentIfNeeded(startingPos, alignment);
            }
        }
        #endregion

        #region AppendFormatted ReadOnlySpan<char>
        /// <summary>Writes the specified character span to the builder.</summary>
        /// <param name="value">The span to write.</param>
        public void AppendFormatted(ReadOnlySpan<char> value)
        {
            // Fast path for when the value fits in the current buffer
            if (value.TryCopyTo(_chars.Slice(_pos)))
            {
                _pos += value.Length;
            }
            else
            {
                GrowThenCopySpan(value);
            }
        }

        /// <summary>Writes the specified string of chars to the builder.</summary>
        /// <param name="value">The span to write.</param>
        /// <param name="alignment">Minimum number of characters that should be written for this value.  If the value is negative, it indicates left-aligned and the required minimum is the absolute value.</param>
        /// <param name="format">The format string.</param>
        public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string? format = null)
        {
            bool leftAlign = false;
            if (alignment < 0)
            {
                leftAlign = true;
                alignment = -alignment;
            }

            int paddingRequired = alignment - value.Length;
            if (paddingRequired <= 0)
            {
                // The value is as large or larger than the required amount of padding,
                // so just write the value.
                AppendFormatted(value);
                return;
            }

            // Write the value along with the appropriate padding.
            EnsureCapacityForAdditionalChars(value.Length + paddingRequired);
            if (leftAlign)
            {
                value.CopyTo(_chars.Slice(_pos));
                _pos += value.Length;
                _chars.Slice(_pos, paddingRequired).Fill(' ');
                _pos += paddingRequired;
            }
            else
            {
                _chars.Slice(_pos, paddingRequired).Fill(' ');
                _pos += paddingRequired;
                value.CopyTo(_chars.Slice(_pos));
                _pos += value.Length;
            }
        }
        #endregion

        #region AppendFormatted string
        /// <summary>Writes the specified value to the builder.</summary>
        /// <param name="value">The value to write.</param>
        public void AppendFormatted(string? value)
        {
            // Fast-path for no custom formatter and a non-null string that fits in the current destination buffer.
            if (_customFormatter is null &&
                value is not null &&
                value.TryCopyTo(_chars.Slice(_pos)))
            {
                _pos += value.Length;
            }
            else
            {
                AppendFormattedSlow(value);
            }
        }

        /// <summary>Writes the specified value to the builder.</summary>
        /// <param name="value">The value to write.</param>
        /// <remarks>
        /// Slow path to handle a custom formatter, potentially null value,
        /// or a string that doesn't fit in the current buffer.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AppendFormattedSlow(string? value)
        {
            if (_customFormatter is null)
            {
                if (value is not null)
                {
                    EnsureCapacityForAdditionalChars(value.Length);
                    value.CopyTo(_chars.Slice(_pos));
                    _pos += value.Length;
                }
            }
            else
            {
                AppendCustomFormatter(value, format: null);
            }
        }

        /// <summary>Writes the specified value to the builder.</summary>
        /// <param name="value">The value to write.</param>
        /// <param name="alignment">Minimum number of characters that should be written for this value.  If the value is negative, it indicates left-aligned and the required minimum is the absolute value.</param>
        /// <param name="format">The format string.</param>
        public void AppendFormatted(string? value, int alignment = 0, string? format = null) =>
            // Format is meaningless for strings and doesn't make sense for someone to specify.  We have the overload
            // simply to disambiguate between ROS<char> and object, just in case someone does specify a format, as
            // string is implicitly convertible to both. Just delegate to the T-based implementation.
            AppendFormatted<string?>(value, alignment, format);
        #endregion

        #region AppendFormatted object
        /// <summary>Writes the specified value to the builder.</summary>
        /// <param name="value">The value to write.</param>
        /// <param name="alignment">Minimum number of characters that should be written for this value.  If the value is negative, it indicates left-aligned and the required minimum is the absolute value.</param>
        /// <param name="format">The format string.</param>
        public void AppendFormatted(object? value, int alignment = 0, string? format = null) =>
            // This overload is expected to be used rarely, only if either a) something strongly typed as object is
            // formatted with both an alignment and a format, or b) the compiler is unable to target type to T. It
            // exists purely to help make cases from (b) compile. Just delegate to the T-based implementation.
            AppendFormatted<object?>(value, alignment, format);
        #endregion
        #endregion

        /// <summary>Formats the value using the custom formatter from the provider.</summary>
        /// <param name="value">The value to write.</param>
        /// <param name="format">The format string.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AppendCustomFormatter<T>(T value, string? format)
        {
            // This case is very rare, but we need to handle it prior to the other checks in case
            // a provider was used that supplied an ICustomFormatter which wanted to intercept the particular value.
            // We do the cast here rather than in the ctor, even though this could be executed multiple times per
            // formatting, to make the cast pay for play.
            Debug.Assert(_customFormatter is not null);
            if (((ICustomFormatter)_customFormatter).Format(format, value, _provider) is string customFormatted)
            {
                AppendLiteral(customFormatted);
            }
        }

        /// <summary>Handles adding any padding required for aligning a formatted value in a hole.</summary>
        /// <param name="startingPos">The position at which the written value started.</param>
        /// <param name="alignment">Non-zero minimum number of characters that should be written for this value.  If the value is negative, it indicates left-aligned and the required minimum is the absolute value.</param>
        private void AppendOrInsertAlignmentIfNeeded(int startingPos, int alignment)
        {
            Debug.Assert(startingPos >= 0 && startingPos <= _pos);
            Debug.Assert(alignment != 0);

            int charsWritten = _pos - startingPos;

            bool leftAlign = false;
            if (alignment < 0)
            {
                leftAlign = true;
                alignment = -alignment;
            }

            int paddingNeeded = alignment - charsWritten;
            if (paddingNeeded > 0)
            {
                EnsureCapacityForAdditionalChars(paddingNeeded);

                if (leftAlign)
                {
                    _chars.Slice(_pos, paddingNeeded).Fill(' ');
                }
                else
                {
                    _chars.Slice(startingPos, charsWritten).CopyTo(_chars.Slice(startingPos + paddingNeeded));
                    _chars.Slice(startingPos, paddingNeeded).Fill(' ');
                }

                _pos += paddingNeeded;
            }
        }

        /// <summary>Ensures <see cref="_chars"/> has the capacity to store <paramref name="additionalChars"/> beyond <see cref="_pos"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureCapacityForAdditionalChars(int additionalChars)
        {
            if (_chars.Length - _pos < additionalChars)
            {
                Grow(additionalChars);
            }
        }

        /// <summary>Fallback for fast path in <see cref="AppendLiteral"/> when there's not enough space in the destination.</summary>
        /// <param name="value">The string to write.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void GrowThenCopyString(string value)
        {
            Grow(value.Length);
            value.CopyTo(_chars.Slice(_pos));
            _pos += value.Length;
        }

        /// <summary>Fallback for <see cref="AppendFormatted(ReadOnlySpan{char})"/> for when not enough space exists in the current buffer.</summary>
        /// <param name="value">The span to write.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void GrowThenCopySpan(ReadOnlySpan<char> value)
        {
            Grow(value.Length);
            value.CopyTo(_chars.Slice(_pos));
            _pos += value.Length;
        }

        /// <summary>Grows <see cref="_chars"/> to have the capacity to store at least <paramref name="additionalChars"/> beyond <see cref="_pos"/>.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)] // keep consumers as streamlined as possible
        private void Grow(int additionalChars)
        {
            // This method is called when the remaining space (_chars.Length - _pos) is
            // insufficient to store a specific number of additional characters.  Thus, we
            // need to grow to at least that new total. GrowCore will handle growing by more
            // than that if possible.
            Debug.Assert(additionalChars > _chars.Length - _pos);
            GrowCore((uint)_pos + (uint)additionalChars);
        }

        /// <summary>Grows the size of <see cref="_chars"/>.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)] // keep consumers as streamlined as possible
        private void Grow()
        {
            // This method is called when the remaining space in _chars isn't sufficient to continue
            // the operation.  Thus, we need at least one character beyond _chars.Length.  GrowCore
            // will handle growing by more than that if possible.
            GrowCore((uint)_chars.Length + 1);
        }

        /// <summary>Grow the size of <see cref="_chars"/> to at least the specified <paramref name="requiredMinCapacity"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)] // but reuse this grow logic directly in both of the above grow routines
        private void GrowCore(uint requiredMinCapacity)
        {
            // We want the max of how much space we actually required and doubling our capacity (without going beyond the max allowed length). We
            // also want to avoid asking for small arrays, to reduce the number of times we need to grow, and since we're working with unsigned
            // ints that could technically overflow if someone tried to, for example, append a huge string to a huge string, we also clamp to int.MaxValue.
            // Even if the array creation fails in such a case, we may later fail in ToStringAndClear.

            uint newCapacity = Math.Max(requiredMinCapacity, Math.Min((uint)_chars.Length * 2, string.MaxLength));
            int arraySize = (int)Math.Clamp(newCapacity, MinimumArrayPoolLength, int.MaxValue);

            char[] newArray = ArrayPool<char>.Shared.Rent(arraySize);
            _chars.Slice(0, _pos).CopyTo(newArray);

            char[]? toReturn = _arrayToReturnToPool;
            _chars = _arrayToReturnToPool = newArray;

            if (toReturn is not null)
            {
                ArrayPool<char>.Shared.Return(toReturn);
            }
        }
    }
}
