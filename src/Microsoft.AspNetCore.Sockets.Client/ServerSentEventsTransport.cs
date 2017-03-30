﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.IO.Pipelines.Text.Primitives;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Formatting;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Sockets.Internal.Formatters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Internal;

namespace Microsoft.AspNetCore.Sockets.Client
{
    public class ServerSentEventsTransport : ITransport
    {
        private static readonly string DefaultUserAgent = "Microsoft.AspNetCore.SignalR.Client/0.0.0";
        private static readonly ProductInfoHeaderValue DefaultUserAgentHeader = ProductInfoHeaderValue.Parse(DefaultUserAgent);

        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _transportCts = new CancellationTokenSource();

        private IChannelConnection<SendMessage, Message> _application;
        private CancellationToken _cancellationToken = new CancellationToken();
        private ServerSentEventsMessageParser _parser = new ServerSentEventsMessageParser();

        public Task Running { get; private set; } = Task.CompletedTask;

        public ServerSentEventsTransport(HttpClient httpClient)
            : this(httpClient, null)
        { }
        public ServerSentEventsTransport(HttpClient httpClient, ILoggerFactory loggerFactory)
        {
            _httpClient = httpClient;
            _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<ServerSentEventsTransport>();
        }

        public Task StartAsync(Uri url, IChannelConnection<SendMessage, Message> application)
        {
            _logger.LogInformation("Starting {0}", nameof(ServerSentEventsTransport));

            _application = application;
            var sseUrl = Utils.AppendPath(url, "sse");
            var sendUrl = Utils.AppendPath(url, "send");
            var sendTask = SendMessages(sendUrl, _cancellationToken);
            var openConnectionTask = OpenConnection(_application, sseUrl, _cancellationToken);

            Running = Task.WhenAll(sendTask, openConnectionTask).ContinueWith(t =>
            {
                _logger.LogDebug("Transport stopped. Exception: '{0}'", t.Exception?.InnerException);

                _application.Output.TryComplete(t.IsFaulted ? t.Exception.InnerException : null);
                return t;
            }).Unwrap();

            return TaskCache.CompletedTask;
        }

        public async Task OpenConnection(IChannelConnection<SendMessage, Message> application, Uri url, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting receive loop");

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            var stream = await response.Content.ReadAsStreamAsync();

            var pipelineReader = stream.AsPipelineReader();

            while (true)
            {
                var result = await pipelineReader.ReadAsync();
                var input = result.Buffer;
                var consumed = input.Start;
                var examined = input.End;

                try
                {
                    if (input.IsEmpty && result.IsCompleted)
                    {
                        _logger.LogDebug("Done reading from pipeline");
                        break;
                    }

                    var parseResult = _parser.ParseMessage(input, out consumed, out examined, out var message);

                    switch (parseResult)
                    {
                        case ServerSentEventsMessageParser.ParsePhase.Completed:
                            break;
                        case ServerSentEventsMessageParser.ParsePhase.Incomplete:
                            if (result.IsCompleted)
                            {
                                throw new FormatException("There was an error parsing");
                            }

                            _parser.Reset();
                            continue;
                        case ServerSentEventsMessageParser.ParsePhase.Error:
                            throw new FormatException("There was an error parsing");
                    }

                    _application.Output.TryWrite(message);
                    break;
                }
                finally
                {
                    pipelineReader.Advance(consumed, examined);
                }

            }

            //using (var streamReader = new StreamReader(stream))
            //{
            //    var eventSourceData = new StringBuilder();
            //    string line = string.Empty;
            //    while ((line = await streamReader.ReadLineAsync()) != null)
            //    {
            //        eventSourceData.Append(line);
            //        if (string.IsNullOrEmpty(line))
            //        {
            //            var parsedData = _parser.ParseSSEMessage(eventSourceData.ToString());
            //            var payload = Encoding.UTF8.GetBytes(eventSourceData.ToString());
            //            var message = new Message(payload, MessageType.Text);
            //            _application.Output.TryWrite(message);
            //            eventSourceData.Clear();
            //        }
            //    }
            //}
        }

        public async Task SendMessages(Uri sendUrl, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting the send loop");

            IList<SendMessage> messages = null;
            try
            {
                while (await _application.Input.WaitToReadAsync(cancellationToken))
                {
                    messages = new List<SendMessage>();
                    while (!cancellationToken.IsCancellationRequested && _application.Input.TryRead(out SendMessage message))
                    {
                        messages.Add(message);
                    }

                    if (messages.Count > 0)
                    {
                        _logger.LogDebug("Sending {0} message(s) to the server using url: {1}", messages.Count, sendUrl);

                        var request = new HttpRequestMessage(HttpMethod.Post, sendUrl);
                        request.Headers.UserAgent.Add(DefaultUserAgentHeader);

                        var memoryStream = new MemoryStream();

                        var pipe = memoryStream.AsPipelineWriter();
                        var output = new PipelineTextOutput(pipe, TextEncoder.Utf8);
                        await WriteMessagesAsync(messages, output, MessageFormat.Binary);

                        memoryStream.Seek(0, SeekOrigin.Begin);

                        request.Content = new StreamContent(memoryStream);
                        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(MessageFormatter.GetContentType(MessageFormat.Binary));

                        var response = await _httpClient.SendAsync(request);
                        response.EnsureSuccessStatusCode();

                        _logger.LogDebug("Message(s) sent successfully");
                        foreach (var message in messages)
                        {
                            message.SendResult?.TrySetResult(null);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogError("Send cancelled");

                if (messages != null)
                {
                    foreach (var message in messages)
                    {
                        message.SendResult?.TrySetCanceled();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Error while sending to '{0}' : '{1}'", sendUrl, ex);
                if (messages != null)
                {
                    foreach (var message in messages)
                    {
                        message.SendResult?.TrySetException(ex);
                    }
                }
                throw;
            }
            finally
            {
                // Make sure the poll loop is terminated
                _transportCts.Cancel();
            }

            _logger.LogInformation("Send loop stopped");
        }

        private async Task WriteMessagesAsync(IList<SendMessage> messages, PipelineTextOutput output, MessageFormat format)
        {
            output.Append(MessageFormatter.GetFormatIndicator(format), TextEncoder.Utf8);

            foreach (var message in messages)
            {
                _logger.LogDebug("Writing '{0}' message to the server", message.Type);

                var payload = message.Payload ?? Array.Empty<byte>();
                if (!MessageFormatter.TryWriteMessage(new Message(payload, message.Type, endOfMessage: true), output, format))
                {
                    // We didn't get any more memory!
                    throw new InvalidOperationException("Unable to write message to pipeline");
                }
                await output.FlushAsync();
            }
        }

        public async Task StopAsync()
        {
            _logger.LogInformation("Transport {0} is stopping", nameof(ServerSentEventsTransport));
            _transportCts.Cancel();
            _application.Output.TryComplete();
            await Running;
        }
    }
}
