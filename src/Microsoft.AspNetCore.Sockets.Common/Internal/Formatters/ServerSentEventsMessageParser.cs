// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.using System;

using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Microsoft.AspNetCore.Sockets.Internal.Formatters
{
    public  class ServerSentEventsMessageParser
    {
        private bool _firstLine = true;

        public ParsePhase ParseMessage(ReadableBuffer buffer, out ReadCursor consumed, out ReadCursor examined, out Message message)
        {
            consumed = buffer.Start;
            examined = buffer.End;
            message = new Message();
            MessageType messageType = MessageType.Text;

            var start = consumed;
            var end = examined;

            while(start != end)
            {
                if (ReadCursorOperations.Seek(start, end, out var found, (byte)'\r') == -1)
                {
                    return ParsePhase.Incomplete;
                }

                var line = ToSpan(buffer.Slice(start, found));

                //Is this the line that indicates the message type
                if (_firstLine)
                {
                    messageType = GetMessageFormat(line);
                    start = buffer.Move(found, 1);
                    _firstLine = false;
                    continue;
                }

                var data = line.Slice(line.IndexOf((byte)' ') + 1);
                message = new Message(data.ToArray(), messageType);
                return ParsePhase.Completed;
            }
            return ParsePhase.Completed;
        }

        public void Reset()
        {
            _firstLine = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private  Span<byte> ToSpan(ReadableBuffer buffer)
        {
            if (buffer.IsSingleSpan)
            {
                return buffer.First.Span;
            }
            return buffer.ToArray();
        }

        private MessageType GetMessageFormat(ReadOnlySpan<byte> line)
        {
            var type = (char)line[6];
            switch (type)
            {
                case 'T':
                    return MessageType.Text;
                case 'B':
                    return MessageType.Binary;
                case 'C':
                    return MessageType.Close;
                case 'E':
                    return MessageType.Error;
                default:
                    throw new FormatException("Invalid mesage type");
            }
        }
        public enum ParsePhase
        {
            Completed,
            Incomplete,
            Error
        }
    }
}
