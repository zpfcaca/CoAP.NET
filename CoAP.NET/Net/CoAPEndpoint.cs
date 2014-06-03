﻿/*
 * Copyright (c) 2011-2014, Longxiang He <helongxiang@smeshlink.com>,
 * SmeshLink Technology Co.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY.
 * 
 * This file is part of the CoAP.NET, a CoAP framework in C#.
 * Please see README for more information.
 */

using System;
using CoAP.Channel;
using CoAP.Codec;
using CoAP.Log;
using CoAP.Stack;

namespace CoAP.Net
{
    /// <summary>
    /// EndPoint encapsulates the stack that executes the CoAP protocol.
    /// </summary>
    public class CoAPEndPoint : IEndPoint, IExchangeForwarder
    {
        static readonly ILogger log = LogManager.GetLogger(typeof(CoAPEndPoint));

        readonly ICoapConfig _config;
        readonly IChannel _channel;
        readonly CoapStack _coapStack;
        private IMessageDeliverer _deliverer;
        private IMatcher _matcher;
        private Int32 _running;
        private System.Net.EndPoint _localEP;

        /// <summary>
        /// Instantiates a new endpoint.
        /// </summary>
        public CoAPEndPoint()
            : this(0, new CoapConfig())
        { }

        /// <summary>
        /// Instantiates a new endpoint with the
        /// specified port and configuration.
        /// </summary>
        public CoAPEndPoint(Int32 port, ICoapConfig config)
            : this(NewUDPChannel(port, config), config)
        { }

        /// <summary>
        /// Instantiates a new endpoint with the
        /// specified <see cref="System.Net.EndPoint"/> and configuration.
        /// </summary>
        public CoAPEndPoint(System.Net.EndPoint localEP, ICoapConfig config)
            : this(NewUDPChannel(localEP, config), config)
        { }

        /// <summary>
        /// Instantiates a new endpoint with the
        /// specified channel and configuration.
        /// </summary>
        public CoAPEndPoint(IChannel channel, ICoapConfig config)
        {
            _config = config;
            _channel = channel;
            _matcher = new Matcher(config);
            _coapStack = new CoapStack(config);
            _channel.DataReceived += ReceiveData;
#if COAPALL
            _spec = config.Spec;
#endif
        }

#if COAPALL
        private ISpec _spec;

        public ISpec Spec
        {
            get { return _spec; }
            set { _spec = value; }
        }
#endif

        /// <inheritdoc/>
        public ICoapConfig Config
        {
            get { return _config; }
        }

        /// <inheritdoc/>
        public System.Net.EndPoint LocalEndPoint
        {
            get { return _localEP; }
        }

        /// <inheritdoc/>
        public IMessageDeliverer MessageDeliverer
        {
            set { _deliverer = value; }
            get
            {
                if (_deliverer == null)
                    _deliverer = new ClientMessageDeliverer();
                return _deliverer;
            }
        }

        /// <inheritdoc/>
        public Boolean Running
        {
            get { return _running > 0; }
        }

        /// <inheritdoc/>
        public void Start()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _running, 1, 0) > 0)
                return;
            _localEP = _channel.LocalEndPoint;
            try
            {
                _matcher.Start();
                _channel.Start();
                _localEP = _channel.LocalEndPoint;
            }
            catch
            {
                if (log.IsWarnEnabled)
                    log.Warn("Cannot start endpoint at " + _localEP);
                Stop();
                throw;
            }
            if (log.IsDebugEnabled)
                log.Debug("Starting endpoint bound to " + _localEP);
        }

        /// <inheritdoc/>
        public void Stop()
        {
            if (System.Threading.Interlocked.Exchange(ref _running, 0) == 0)
                return;
            if (log.IsDebugEnabled)
                log.Debug("Stopping endpoint bound to " + _localEP);
            _channel.Stop();
            _matcher.Stop();
            _matcher.Clear();
        }

        /// <inheritdoc/>
        public void Clear()
        {
            _matcher.Clear();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (Running)
                Stop();
            _channel.Dispose();
            IDisposable d = _matcher as IDisposable;
            if (d != null)
                d.Dispose();
        }

        /// <inheritdoc/>
        public void SendRequest(Request request)
        {
            // TODO thread
            _coapStack.SendRequest(request, MessageDeliverer);
        }

        /// <inheritdoc/>
        public void SendResponse(Exchange exchange, Response response)
        {
            // TODO thread
            _coapStack.SendResponse(exchange, response);
        }

        /// <inheritdoc/>
        public void SendEmptyMessage(Exchange exchange, EmptyMessage message)
        {
            // TODO thread
            _coapStack.SendEmptyMessage(exchange, message);
        }

        private void ReceiveData(Object sender, DataReceivedEventArgs e)
        {
            // TODO new thread
            // TODO may have more or less than one message in the incoming bytes

            IMessageDecoder decoder = Spec.NewMessageDecoder(e.Data);
            if (decoder.IsRequest)
            {
                Request request;
                try
                {
                    request = decoder.DecodeRequest();
                }
                catch (InvalidOperationException)
                {
                    if (decoder.IsReply)
                    {
                        if (log.IsWarnEnabled)
                            log.Warn("Message format error caused by " + e.EndPoint);
                    }
                    else
                    {
                        // manually build RST from raw information
                        EmptyMessage rst = new EmptyMessage(MessageType.RST);
                        rst.Destination = e.EndPoint;
                        rst.ID = decoder.ID;
                        _channel.Send(Serialize(rst), rst.Destination);

                        if (log.IsWarnEnabled)
                            log.Warn("Message format error caused by " + e.EndPoint + " and reseted.");
                    }
                    return;
                }

                request.Source = e.EndPoint;
                Exchange exchange = _matcher.ReceiveRequest(request);
                if (exchange != null)
                {
                    exchange.Forwarder = this;
                    exchange.EndPoint = this;
                    _coapStack.ReceiveRequest(exchange, request);
                }
            }
            else if (decoder.IsResponse)
            {
                Response response = decoder.DecodeResponse();
                response.Source = e.EndPoint;

                Exchange exchange = _matcher.ReceiveResponse(response);
                if (exchange != null)
                {
                    response.RTT = (DateTime.Now - exchange.Timestamp).TotalMilliseconds;
                    exchange.Forwarder = this;
                    exchange.EndPoint = this;
                    _coapStack.ReceiveResponse(exchange, response);
                }
            }
            else if (decoder.IsEmpty)
            {
                EmptyMessage message = decoder.DecodeEmptyMessage();
                message.Source = e.EndPoint;

                // CoAP Ping
                if (message.Type == MessageType.CON || message.Type == MessageType.NON)
                {
                    EmptyMessage rst = EmptyMessage.NewRST(message);

                    if (log.IsDebugEnabled)
                        log.Debug("Responding to ping by " + e.EndPoint);

                    _channel.Send(Serialize(rst), rst.Destination);
                }
                else
                {
                    Exchange exchange = _matcher.ReceiveEmptyMessage(message);
                    if (exchange != null)
                    {
                        exchange.Forwarder = this;
                        exchange.EndPoint = this;
                        _coapStack.ReceiveEmptyMessage(exchange, message);
                    }
                }
            }
            else if (log.IsDebugEnabled)
            {
                log.Debug("Silently ignoring non-CoAP message from " + e.EndPoint);
            }
        }

        private Byte[] Serialize(EmptyMessage message)
        {
            Byte[] bytes = message.Bytes;
            if (bytes == null)
            {
                bytes = Spec.NewMessageEncoder().Encode(message);
                message.Bytes = bytes;
            }
            return bytes;
        }

        private Byte[] Serialize(Request request)
        {
            Byte[] bytes = request.Bytes;
            if (bytes == null)
            {
                bytes = Spec.NewMessageEncoder().Encode(request);
                request.Bytes = bytes;
            }
            return bytes;
        }

        private Byte[] Serialize(Response response)
        {
            Byte[] bytes = response.Bytes;
            if (bytes == null)
            {
                bytes = Spec.NewMessageEncoder().Encode(response);
                response.Bytes = bytes;
            }
            return bytes;
        }

        static IChannel NewUDPChannel(Int32 port, ICoapConfig config)
        {
            UDPChannel channel = new UDPChannel(port);
            // TODO config
            return channel;
        }

        static IChannel NewUDPChannel(System.Net.EndPoint localEP, ICoapConfig config)
        {
            UDPChannel channel = new UDPChannel(localEP);
            // TODO config
            return channel;
        }

        void IExchangeForwarder.SendRequest(Exchange exchange, Request request)
        {
            _matcher.SendRequest(exchange, request);

            if (!request.Canceled)
                _channel.Send(Serialize(request), request.Destination);
        }

        void IExchangeForwarder.SendResponse(Exchange exchange, Response response)
        {
            _matcher.SendResponse(exchange, response);

            if (!response.Canceled)
                _channel.Send(Serialize(response), response.Destination);
        }

        void IExchangeForwarder.SendEmptyMessage(Exchange exchange, EmptyMessage message)
        {
            _matcher.SendEmptyMessage(exchange, message);

            if (!message.Canceled)
                _channel.Send(Serialize(message), message.Destination);
        }
    }
}