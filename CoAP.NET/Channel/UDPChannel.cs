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
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace CoAP.Channel
{
    /// <summary>
    /// Channel via UDP protocol.
    /// </summary>
    public class UDPChannel : IChannel
    {
        /// <summary>
        /// Default size of buffer for receiving packet.
        /// </summary>
        public const Int32 DefaultReceivePacketSize = 4096;
        private Int32 _receiveBufferSize;
        private Int32 _sendBufferSize;
        private Int32 _receivePacketSize = DefaultReceivePacketSize;
        private Int32 _port;
        private System.Net.EndPoint _localEP;
        private UDPSocket _socket;
        private UDPSocket _socketBackup;
        private Int32 _running;
        private Int32 _writing;
        private readonly ConcurrentQueue<RawData> _sendingQueue = new ConcurrentQueue<RawData>();

        /// <inheritdoc/>
        public event EventHandler<DataReceivedEventArgs> DataReceived;

        /// <summary>
        /// Initializes a UDP channel with a random port.
        /// </summary>
        public UDPChannel() 
            : this(0)
        { 
        }

        /// <summary>
        /// Initializes a UDP channel with the given port, both on IPv4 and IPv6.
        /// </summary>
        public UDPChannel(Int32 port)
        {
            _port = port;
        }

        /// <summary>
        /// Initializes a UDP channel with the specific endpoint.
        /// </summary>
        public UDPChannel(System.Net.EndPoint localEP)
        {
            _localEP = localEP;
        }

        /// <inheritdoc/>
        public System.Net.EndPoint LocalEndPoint
        {
            get
            {
                return _socket == null
                    ? (_localEP ?? new IPEndPoint(IPAddress.IPv6Any, _port))
                    : _socket.Socket.LocalEndPoint;
            }
        }

        public Int32 ReceiveBufferSize
        {
            get { return _receiveBufferSize; }
            set { _receiveBufferSize = value; }
        }

        public Int32 SendBufferSize
        {
            get { return _sendBufferSize; }
            set { _sendBufferSize = value; }
        }

        public Int32 ReceivePacketSize
        {
            get { return _receivePacketSize; }
            set { _receivePacketSize = value; }
        }

        /// <inheritdoc/>
        public void Start()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _running, 1, 0) > 0)
                return;

            if (_localEP == null)
            {
                _socket = new UDPSocket(AddressFamily.InterNetworkV6, _receivePacketSize + 1); // +1 to check for > ReceivePacketSize

                try
                {
                    // Enable IPv4-mapped IPv6 addresses to accept both IPv6 and IPv4 connections in a same socket.
                    _socket.Socket.SetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)27, 0);
                }
                catch
                {
                    // IPv4-mapped address seems not to be supported, set up a separated socket of IPv4.
                    _socketBackup = new UDPSocket(AddressFamily.InterNetwork, _receivePacketSize + 1);
                }

                _socket.Socket.Bind(new IPEndPoint(IPAddress.IPv6Any, _port));
                if (_socketBackup != null)
                    _socketBackup.Socket.Bind(new IPEndPoint(IPAddress.Any, _port));
            }
            else
            {
                _socket = new UDPSocket(_localEP.AddressFamily, _receivePacketSize + 1);
                _socket.Socket.Bind(_localEP);
            }

            if (_receiveBufferSize > 0)
            {
                _socket.Socket.ReceiveBufferSize = _receiveBufferSize;
                if (_socketBackup != null)
                    _socketBackup.Socket.ReceiveBufferSize = _receiveBufferSize;
            }
            if (_sendBufferSize > 0)
            {
                _socket.Socket.SendBufferSize = _sendBufferSize;
                if (_socketBackup != null)
                    _socketBackup.Socket.SendBufferSize = _sendBufferSize;
            }

            BeginReceive();
        }

        /// <inheritdoc/>
        public void Stop()
        {
            if (System.Threading.Interlocked.Exchange(ref _running, 0) == 0)
                return;

            if (_socket != null)
            {
                _socket.Socket.Close();
                _socket = null;
            }
            if (_socketBackup != null)
            {
                _socketBackup.Socket.Close();
                _socketBackup = null;
            }
        }

        /// <inheritdoc/>
        public void Send(Byte[] data, System.Net.EndPoint ep)
        {
            RawData raw = new RawData();
            raw.Data = data;
            raw.EndPoint = ep;
            _sendingQueue.Enqueue(raw);
            if (System.Threading.Interlocked.CompareExchange(ref _writing, 1, 0) > 0)
                return;
            BeginSend();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Stop();
        }

        private void BeginReceive()
        {
            if (_running > 0)
            {
                System.Net.EndPoint remoteEP = new IPEndPoint(
                    _socket.Socket.AddressFamily == AddressFamily.InterNetwork ?
                    IPAddress.Any : IPAddress.IPv6Any, 0);

                try
                {
                    _socket.Socket.BeginReceiveFrom(_socket.Buffer, 0, _socket.Buffer.Length,
                        SocketFlags.None, ref remoteEP, ReceiveCallback, _socket);
                }
                catch (ObjectDisposedException)
                {
                    // do nothing
                }

                if (_socketBackup != null)
                {
                    System.Net.EndPoint remoteV4 = new IPEndPoint(IPAddress.Any, 0);
                    try
                    {
                        _socketBackup.Socket.BeginReceiveFrom(_socketBackup.Buffer, 0, _socketBackup.Buffer.Length,
                            SocketFlags.None, ref remoteV4, ReceiveCallback, _socketBackup);
                    }
                    catch (ObjectDisposedException)
                    {
                        // do nothing
                    }
                }
            }
        }

        private void ReceiveCallback(IAsyncResult ar)
        {
            UDPSocket socket = (UDPSocket)ar.AsyncState;
            System.Net.EndPoint remoteEP = new IPEndPoint(
                socket.Socket.AddressFamily == AddressFamily.InterNetwork ?
                IPAddress.Any : IPAddress.IPv6Any, 0);

            Int32 count = 0;
            try
            {
                count = socket.Socket.EndReceiveFrom(ar, ref remoteEP);
            }
            catch (ObjectDisposedException)
            {
                // do nothing
                return;
            }
            catch (SocketException)
            {
                // ignore it
                //if (log.IsFatalEnabled)
                //    log.Fatal("UDPLayer - Failed receive datagram", ex);
            }
            
            if (count > 0)
            {
                Byte[] bytes = new Byte[count];
                Buffer.BlockCopy(socket.Buffer, 0, bytes, 0, count);
                if (remoteEP.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    IPEndPoint ipep = (IPEndPoint)remoteEP;
                    if (IsIPv4MappedToIPv6(ipep.Address))
                        ipep.Address = MapToIPv4(ipep.Address);
                }
                FireDataReceived(bytes, remoteEP);
            }

            BeginReceive();
        }

        private void FireDataReceived(Byte[] data, System.Net.EndPoint ep)
        {
            EventHandler<DataReceivedEventArgs> h = DataReceived;
            if (h != null)
                h(this, new DataReceivedEventArgs(data, ep));
        }

        private void BeginSend()
        {
            if (_running == 0)
                return;

            RawData raw;
            if (!_sendingQueue.TryDequeue(out raw))
            {
                System.Threading.Interlocked.Exchange(ref _writing, 0);
                return;
            }

            UDPSocket socket = _socket;
            IPEndPoint remoteEP = (IPEndPoint)raw.EndPoint;

            if (remoteEP.AddressFamily == AddressFamily.InterNetwork)
            {
                if (_socketBackup != null)
                {
                    // use the separated socket of IPv4 to deal with IPv4 conversions.
                    socket = _socketBackup;
                }
                else if (_socket.Socket.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    remoteEP = new IPEndPoint(MapToIPv6(remoteEP.Address), remoteEP.Port);
                }
            }

            try
            {
                socket.Socket.BeginSendTo(raw.Data, 0, raw.Data.Length, SocketFlags.None, remoteEP, SendCallback, socket);
            }
            catch (ObjectDisposedException)
            {
                // do nothing
            }
        }

        private void SendCallback(IAsyncResult ar)
        {
            UDPSocket socket = (UDPSocket)ar.AsyncState;
            try
            {
                socket.Socket.EndSendTo(ar);
            }
            catch (ObjectDisposedException)
            {
                // do nothing
                return;
            }
            BeginSend();
        }

        static Boolean IsIPv4MappedToIPv6(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetworkV6)
                return false;
            Byte[] bytes = address.GetAddressBytes();
            for (Int32 i = 0; i < 10; i++)
            {
                if (bytes[i] != 0)
                    return false;
            }
            return bytes[10] == 0xFF && bytes[11] == 0xFF;
        }

        static IPAddress MapToIPv4(IPAddress address)
        {
            if (address.AddressFamily == AddressFamily.InterNetwork)
                return address;
            Byte[] bytes = address.GetAddressBytes();
            Int64 newAddress = (bytes[12] & 0xff) | (bytes[13] & 0xff) << 8 | (bytes[14] & 0xff) << 16 | (bytes[15] & 0xff) << 24;
            return new IPAddress(newAddress);
        }

        static IPAddress MapToIPv6(IPAddress address)
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
                return address;
            Byte[] bytes = address.GetAddressBytes();
            Byte[] newAddress = new Byte[16] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff, bytes[0], bytes[1], bytes[2], bytes[3] };
            return new IPAddress(newAddress);
        }

        class UDPSocket
        {
            public readonly Socket Socket;
            public readonly Byte[] Buffer;

            public UDPSocket(AddressFamily addressFamily, Int32 bufferSize)
            {
                Socket = new Socket(addressFamily, SocketType.Dgram, ProtocolType.Udp);
                Buffer = new Byte[bufferSize];
            }
        }

        class RawData
        {
            public Byte[] Data;
            public System.Net.EndPoint EndPoint;
        }
    }
}
