// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.IO.Pipelines;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Http.Parser.Internal;

namespace System.Text.Http.Parser
{
    public class HttpParser : IHttpParser
    {
        // TODO: commented out;
        //public ILogger Log { get; set; }

        // byte types don't have a data type annotation so we pre-cast them; to avoid in-place casts
        private const byte ByteCR = (byte)'\r';
        private const byte ByteLF = (byte)'\n';
        private const byte ByteColon = (byte)':';
        private const byte ByteSpace = (byte)' ';
        private const byte ByteTab = (byte)'\t';
        private const byte ByteQuestionMark = (byte)'?';
        private const byte BytePercentage = (byte)'%';

        public unsafe bool ParseRequestLine<T>(T handler, ReadableBuffer buffer, out ReadCursor consumed, out ReadCursor examined) where T : IHttpRequestLineHandler
        {
            consumed = buffer.Start;
            examined = buffer.End;

            // Prepare the first span
            var span = buffer.First.Span;
            var lineIndex = span.IndexOf(ByteLF);
            if (lineIndex >= 0)
            {
                consumed = buffer.Move(consumed, lineIndex + 1);
                span = span.Slice(0, lineIndex + 1);
            }
            else if (buffer.IsSingleSpan || !TryGetNewLineSpan(ref buffer, ref span, out consumed))
            {
                // No request line end
                return false;
            }

            // Fix and parse the span
            fixed (byte* data = &span.DangerousGetPinnableReference())
            {
                ParseRequestLine(handler, data, span.Length);
            }

            examined = consumed;
            return true;
        }

        static readonly byte[] s_Eol = Encoding.UTF8.GetBytes("\r\n");
        public unsafe bool ParseRequestLine<T>(ref T handler, ReadOnlyBytes buffer, out int consumed) where T : IHttpRequestLineHandler
        {
            // Prepare the first span
            var span = buffer.First.Span;
            var lineIndex = span.IndexOf(ByteLF);
            if (lineIndex >= 0)
            {
                consumed = lineIndex + 1;
                span = span.Slice(0, lineIndex + 1);
            }
            else
            {
                var eol = buffer.IndexOf(s_Eol);
                if (eol == -1)
                {
                    consumed = 0;
                    return false;
                }

                span = buffer.Slice(0, eol + s_Eol.Length).ToSpan();
            }

            // Fix and parse the span
            fixed (byte* data = &span.DangerousGetPinnableReference())
            {
                ParseRequestLine(handler, data, span.Length);
            }

            consumed = span.Length;
            return true;
        }

        private unsafe void ParseRequestLine<T>(T handler, byte* data, int length) where T : IHttpRequestLineHandler
        {
            int offset;
            ReadOnlySpan<byte> customMethod;
            // Get Method and set the offset
            var method = HttpUtilities.GetKnownMethod(data, length, out offset);
            if (method == Http.Method.Custom)
            {
                customMethod = GetUnknownMethod(data, length, out offset);
            }

            // Skip space
            offset++;

            byte ch = 0;
            // Target = Path and Query
            var pathEncoded = false;
            var pathStart = -1;
            for (; offset < length; offset++)
            {
                ch = data[offset];
                if (ch == ByteSpace)
                {
                    if (pathStart == -1)
                    {
                        // Empty path is illegal
                        RejectRequestLine(data, length);
                    }

                    break;
                }
                else if (ch == ByteQuestionMark)
                {
                    if (pathStart == -1)
                    {
                        // Empty path is illegal
                        RejectRequestLine(data, length);
                    }

                    break;
                }
                else if (ch == BytePercentage)
                {
                    if (pathStart == -1)
                    {
                        // Path starting with % is illegal
                        RejectRequestLine(data, length);
                    }

                    pathEncoded = true;
                }
                else if (pathStart == -1)
                {
                    pathStart = offset;
                }
            }

            if (pathStart == -1)
            {
                // Start of path not found
                RejectRequestLine(data, length);
            }

            var pathBuffer = new ReadOnlySpan<byte>(data + pathStart, offset - pathStart);

            // Query string
            var queryStart = offset;
            if (ch == ByteQuestionMark)
            {
                // We have a query string
                for (; offset < length; offset++)
                {
                    ch = data[offset];
                    if (ch == ByteSpace)
                    {
                        break;
                    }
                }
            }

            // End of query string not found
            if (offset == length)
            {
                RejectRequestLine(data, length);
            }

            var targetBuffer = new ReadOnlySpan<byte>(data + pathStart, offset - pathStart);
            var query = new ReadOnlySpan<byte>(data + queryStart, offset - queryStart);

            // Consume space
            offset++;

            // Version
            var httpVersion = HttpUtilities.GetKnownVersion(data + offset, length - offset);
            if (httpVersion == Http.Version.Unknown)
            {
                if (data[offset] == ByteCR || data[length - 2] != ByteCR)
                {
                    // If missing delimiter or CR before LF, reject and log entire line
                    RejectRequestLine(data, length);
                }
                else
                {
                    // else inform HTTP version is unsupported.
                    RejectUnknownVersion(data + offset, length - offset - 2);
                }
            }

            // After version's 8 bytes and CR, expect LF
            if (data[offset + 8 + 1] != ByteLF)
            {
                RejectRequestLine(data, length);
            }

            var line = new ReadOnlySpan<byte>(data, length);

            handler.OnStartLine(method, httpVersion, targetBuffer, pathBuffer, query, customMethod, pathEncoded);
        }

        public unsafe bool ParseHeaders<T>(T handler, ReadableBuffer buffer, out ReadCursor consumed, out ReadCursor examined, out int consumedBytes) where T : IHttpHeadersHandler
        {
            consumed = buffer.Start;
            examined = buffer.End;
            consumedBytes = 0;

            var bufferEnd = buffer.End;

            var reader = new ReadableBufferReader(buffer);
            var start = default(ReadableBufferReader);
            var done = false;

            try
            {
                while (!reader.End)
                {
                    var span = reader.Span;
                    var remaining = span.Length - reader.Index;

                    fixed (byte* pBuffer = &span.DangerousGetPinnableReference())
                    {
                        while (remaining > 0)
                        {
                            var index = reader.Index;
                            int ch1;
                            int ch2;

                            // Fast path, we're still looking at the same span
                            if (remaining >= 2)
                            {
                                ch1 = pBuffer[index];
                                ch2 = pBuffer[index + 1];
                            }
                            else
                            {
                                // Store the reader before we look ahead 2 bytes (probably straddling
                                // spans)
                                start = reader;

                                // Possibly split across spans
                                ch1 = reader.Take();
                                ch2 = reader.Take();
                            }

                            if (ch1 == ByteCR)
                            {
                                // Check for final CRLF.
                                if (ch2 == -1)
                                {
                                    // Reset the reader so we don't consume anything
                                    reader = start;
                                    return false;
                                }
                                else if (ch2 == ByteLF)
                                {
                                    // If we got 2 bytes from the span directly so skip ahead 2 so that
                                    // the reader's state matches what we expect
                                    if (index == reader.Index)
                                    {
                                        reader.Skip(2);
                                    }

                                    done = true;
                                    return true;
                                }

                                // Headers don't end in CRLF line.
                                RejectRequest(RequestRejectionReason.InvalidRequestHeadersNoCRLF);
                            }

                            // We moved the reader so look ahead 2 bytes so reset both the reader
                            // and the index
                            if (index != reader.Index)
                            {
                                reader = start;
                                index = reader.Index;
                            }

                            var endIndex = new ReadOnlySpan<byte>(pBuffer + index, remaining).IndexOf(ByteLF);
                            var length = 0;

                            if (endIndex != -1)
                            {
                                length = endIndex + 1;
                                var pHeader = pBuffer + index;

                                TakeSingleHeader(pHeader, length, handler);
                            }
                            else
                            {
                                var current = reader.Cursor;

                                // Split buffers
                                if (ReadCursorOperations.Seek(current, bufferEnd, out var lineEnd, ByteLF) == -1)
                                {
                                    // Not there
                                    return false;
                                }

                                // Make sure LF is included in lineEnd
                                lineEnd = buffer.Move(lineEnd, 1);
                                var headerSpan = buffer.Slice(current, lineEnd).ToSpan();
                                length = headerSpan.Length;

                                fixed (byte* pHeader = &headerSpan.DangerousGetPinnableReference())
                                {
                                    TakeSingleHeader(pHeader, length, handler);
                                }

                                // We're going to the next span after this since we know we crossed spans here
                                // so mark the remaining as equal to the headerSpan so that we end up at 0
                                // on the next iteration
                                remaining = length;
                            }

                            // Skip the reader forward past the header line
                            reader.Skip(length);
                            remaining -= length;
                        }
                    }
                }

                return false;
            }
            finally
            {
                consumed = reader.Cursor;
                consumedBytes = reader.ConsumedBytes;

                if (done)
                {
                    examined = consumed;
                }
            }
        }

        public unsafe bool ParseHeaders<T>(T handler, ReadOnlyBytes buffer, out int consumedBytes) where T : class, IHttpHeadersHandler
        {
            return ParseHeaders(ref handler, buffer, out consumedBytes);
        }

        public unsafe bool ParseHeaders<T>(ref T handler, ReadOnlyBytes buffer, out int consumedBytes) where T : IHttpHeadersHandler
        {
            var index = 0;        
            var rest = buffer.Rest;
            var span = buffer.First.Span;
            var remaining = span.Length;
            consumedBytes = 0;
            while (true) {
                fixed (byte* pBuffer = &span.DangerousGetPinnableReference()) {
                    while (remaining > 0) {

                        int ch1;
                        int ch2;

                        // Fast path, we're still looking at the same span
                        if (remaining >= 2) {
                            ch1 = pBuffer[index];
                            ch2 = pBuffer[index + 1];
                        }
                        // Slow Path
                        else {
                            ReadNextTwoChars(pBuffer, remaining, index, out ch1, out ch2, rest);
                        }

                        if (ch1 == ByteCR) {
                            if (ch2 == ByteLF) {
                                consumedBytes += 2;
                                return true;
                            }

                            if (ch2 == -1) {
                                consumedBytes = 0;
                                return false;
                            }

                            // Headers don't end in CRLF line.
                            RejectRequest(RequestRejectionReason.InvalidRequestHeadersNoCRLF);
                        }

                        var endIndex = new ReadOnlySpan<byte>(pBuffer + index, remaining).IndexOf(ByteLF);
                        var length = 0;

                        if (endIndex != -1) {
                            length = endIndex + 1;
                            var pHeader = pBuffer + index;

                            TakeSingleHeader(pHeader, length, handler);
                        }
                        else {
                            // Split buffers
                            var end = buffer.Slice(index).IndexOf(ByteLF);
                            if (end == -1) {
                                // Not there
                                consumedBytes = 0;
                                return false;
                            }

                            var headerSpan = buffer.Slice(index, end - index + 1).ToSpan();
                            length = headerSpan.Length;

                            fixed (byte* pHeader = &headerSpan.DangerousGetPinnableReference()) {
                                TakeSingleHeader(pHeader, length, handler);
                            }
                        }

                        // Skip the reader forward past the header line
                        index += length;
                        consumedBytes += length;
                        remaining -= length;
                    }
                }

                if (rest != null) {
                    span = rest.First.Span;
                    rest = rest.Rest;
                    remaining = span.Length + remaining;
                    index = span.Length - remaining;
                }
                else {
                    consumedBytes = 0;
                    return false;
                }
            }
        }

        private static unsafe void ReadNextTwoChars(byte* pBuffer, int remaining, int index, out int ch1, out int ch2, IReadOnlyBufferList<byte> rest)
        {
            if (remaining == 1) {
                ch1 = pBuffer[index];
                if (rest == null) {
                    ch2 = -1;
                }
                else {
                    ch2 = rest.First.Span[0];
                }
            }
            else {
                ch1 = -1;
                ch2 = -1;
                if (rest != null) {
                    var nextSpan = rest.First.Span;
                    if (nextSpan.Length > 0) {
                        ch1 = nextSpan[0];

                        if (nextSpan.Length > 1) {
                            ch2 = nextSpan[1];
                        }
                        else {
                            ch2 = -1;
                        }
                    }
                    else {
                        ch2 = -1;
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe int FindEndOfName(byte* headerLine, int length)
        {
            var index = 0;
            var sawWhitespace = false;
            for (; index < length; index++)
            {
                var ch = headerLine[index];
                if (ch == ByteColon)
                {
                    break;
                }
                if (ch == ByteTab || ch == ByteSpace || ch == ByteCR)
                {
                    sawWhitespace = true;
                }
            }

            if (index == length || sawWhitespace)
            {
                RejectRequestHeader(headerLine, length);
            }

            return index;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe void TakeSingleHeader<T>(byte* headerLine, int length, T handler) where T : IHttpHeadersHandler
        {
            // Skip CR, LF from end position
            var valueEnd = length - 3;
            var nameEnd = FindEndOfName(headerLine, length);

            if (headerLine[valueEnd + 2] != ByteLF)
            {
                RejectRequestHeader(headerLine, length);
            }
            if (headerLine[valueEnd + 1] != ByteCR)
            {
                RejectRequestHeader(headerLine, length);
            }

            // Skip colon from value start
            var valueStart = nameEnd + 1;
            // Ignore start whitespace
            for (; valueStart < valueEnd; valueStart++)
            {
                var ch = headerLine[valueStart];
                if (ch != ByteTab && ch != ByteSpace && ch != ByteCR)
                {
                    break;
                }
                else if (ch == ByteCR)
                {
                    RejectRequestHeader(headerLine, length);
                }
            }

            // Check for CR in value
            var i = valueStart + 1;
            if (Contains(headerLine + i, valueEnd - i, ByteCR))
            {
                RejectRequestHeader(headerLine, length);
            }

            // Ignore end whitespace
            for (; valueEnd >= valueStart; valueEnd--)
            {
                var ch = headerLine[valueEnd];
                if (ch != ByteTab && ch != ByteSpace)
                {
                    break;
                }
            }

            var nameBuffer = new ReadOnlySpan<byte>(headerLine, nameEnd);
            var valueBuffer = new ReadOnlySpan<byte>(headerLine + valueStart, valueEnd - valueStart + 1);

            handler.OnHeader(nameBuffer, valueBuffer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe bool Contains(byte* searchSpace, int length, byte value)
        {
            var i = 0;
            if (Vector.IsHardwareAccelerated)
            {
                // Check Vector lengths
                if (length - Vector<byte>.Count >= i)
                {
                    var vValue = GetVector(value);
                    do
                    {
                        if (!Vector<byte>.Zero.Equals(Vector.Equals(vValue, Unsafe.Read<Vector<byte>>(searchSpace + i))))
                        {
                            goto found;
                        }

                        i += Vector<byte>.Count;
                    } while (length - Vector<byte>.Count >= i);
                }
            }

            // Check remaining for CR
            for (; i <= length; i++)
            {
                var ch = searchSpace[i];
                if (ch == value)
                {
                    goto found;
                }
            }
            return false;
            found:
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryGetNewLineSpan(ref ReadableBuffer buffer, ref Span<byte> span, out ReadCursor end)
        {
            var start = buffer.Start;
            if (ReadCursorOperations.Seek(start, buffer.End, out end, ByteLF) != -1)
            {
                // Move 1 byte past the \n
                end = buffer.Move(end, 1);
                span = buffer.Slice(start, end).ToSpan();
                return true;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private unsafe ReadOnlySpan<byte> GetUnknownMethod(byte* data, int length, out int methodLength)
        {
            methodLength = 0;
            for (var i = 0; i < length; i++)
            {
                var ch = data[i];

                if (ch == ByteSpace)
                {
                    if (i == 0)
                    {
                        RejectRequestLine(data, length);
                    }

                    methodLength = i;
                    break;
                }
                else if (!IsValidTokenChar((char)ch))
                {
                    RejectRequestLine(data, length);
                }
            }

            return new ReadOnlySpan<byte>(data, methodLength);
        }

        // TODO: this could be optimized by using a table
        private static bool IsValidTokenChar(char c)
        {
            // Determines if a character is valid as a 'token' as defined in the
            // HTTP spec: https://tools.ietf.org/html/rfc7230#section-3.2.6
            return
                (c >= '0' && c <= '9') ||
                (c >= 'A' && c <= 'Z') ||
                (c >= 'a' && c <= 'z') ||
                c == '!' ||
                c == '#' ||
                c == '$' ||
                c == '%' ||
                c == '&' ||
                c == '\'' ||
                c == '*' ||
                c == '+' ||
                c == '-' ||
                c == '.' ||
                c == '^' ||
                c == '_' ||
                c == '`' ||
                c == '|' ||
                c == '~';
        }

        private void RejectRequest(RequestRejectionReason reason)
            => throw BadHttpRequestException.GetException(reason);

        private unsafe void RejectRequestLine(byte* requestLine, int length)
            => throw GetInvalidRequestException(RequestRejectionReason.InvalidRequestLine, requestLine, length);

        private unsafe void RejectRequestHeader(byte* headerLine, int length)
            => throw GetInvalidRequestException(RequestRejectionReason.InvalidRequestHeader, headerLine, length);

        private unsafe void RejectUnknownVersion(byte* version, int length)
            => throw GetInvalidRequestException(RequestRejectionReason.UnrecognizedHTTPVersion, version, length);

        private unsafe BadHttpRequestException GetInvalidRequestException(RequestRejectionReason reason, byte* detail, int length)
        {
            return BadHttpRequestException.GetException(reason, "");
        }

        public void Reset()
        {
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector<byte> GetVector(byte vectorByte)
        {
            // Vector<byte> .ctor doesn't become an intrinsic due to detection issue
            // However this does cause it to become an intrinsic (with additional multiply and reg->reg copy)
            // https://github.com/dotnet/coreclr/issues/7459#issuecomment-253965670
            return Vector.AsVectorByte(new Vector<uint>(vectorByte * 0x01010101u));
        }

        enum State : byte
        {
            ParsingName,
            BeforValue,
            ParsingValue,
            ArferCR,
        }
    }
}
