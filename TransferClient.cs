/*Multiple FileTransfer is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    Foobar is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with Multiple FileTransfer.  If not, see <http://www.gnu.org/licenses/>.*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.IO;

namespace FileTransfer
{
    public delegate void TranferEventHandler(object sender, TransferQueue queue);
    public delegate void ConnectCallback(object sender, string error);

    public class TransferClient
    {
        private Socket _baseSocket;

        private byte[] _buffer = new byte[8192];

        private ConnectCallback _connectCallback;

        private Dictionary<int, TransferQueue> _transfer = new Dictionary<int, TransferQueue>();

        public Dictionary<int, TransferQueue> Transfers
        {
            get { return _transfer; }
        }

        public bool Closed
        {
            get;
            private set;
        }

        public string OutputFolder
        {
            get;
            set;
        }

        public IPEndPoint EndPoint
        {
            get;
            private set;
        }

        public event TranferEventHandler Queued;
        public event TranferEventHandler ProgressChanged;
        public event TranferEventHandler Stopped;
        public event TranferEventHandler Complete;
        public event EventHandler Disconnected;

        public TransferClient()
        {
            _baseSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }

        public TransferClient(Socket socket)
        {
            _baseSocket = socket;
            EndPoint = (IPEndPoint)_baseSocket.RemoteEndPoint;
        }

        public void Connect(string hostName, int port, ConnectCallback callBack)
        {
            _connectCallback = callBack;
            _baseSocket.BeginConnect(hostName, port, connectCallback, null);
        }

        private void connectCallback(IAsyncResult ar)
        {
            string error = null;
            try
            {
                _baseSocket.EndConnect(ar);
                EndPoint = (IPEndPoint)_baseSocket.RemoteEndPoint;
            }
            catch (Exception e)
            {
                error = e.Message;                
            }

            _connectCallback(this, error);
        }

        public void Run()
        {
            try
            {
                _baseSocket.BeginReceive(_buffer, 0, _buffer.Length, SocketFlags.Peek, receiveCallback, null);
            }
            catch
            {
                Close();
            }
        }

        public void QueueTransfer(string fileName)
        {
            try
            {
                TransferQueue queue = TransferQueue.CreateUploadQueue(this, fileName);
                _transfer.Add(queue.ID, queue);
                PacketWriter pw = new PacketWriter();
                pw.Write((byte)Headers.Queue);
                pw.Write(queue.ID);
                pw.Write(queue.Filename);
                pw.Write(queue.Lenght);
                Send(pw.GetBytes());

                if (Queued != null)
                {
                    Queued(this, queue);
                }
            }
            catch 
            {

            }
        }

        public void StartTransfer(TransferQueue queue)
        {
            PacketWriter pw = new PacketWriter();
            pw.Write((byte)Headers.Start);
            pw.Write(queue.ID);
            Send(pw.GetBytes());
        }

        public void StopTransfer(TransferQueue queue)
        {
            if (queue.Type == QueueType.Upload)
            {
                queue.Stop();
            }

            PacketWriter pw = new PacketWriter();
            pw.Write((byte)Headers.Stop);
            pw.Write(queue.ID);
            Send(pw.GetBytes());
            queue.Close();
        }

        public void PauseTransfer(TransferQueue queue)
        {
            if (queue.Type == QueueType.Upload)
            {
                queue.Pause();
                return;
            }

            PacketWriter pw = new PacketWriter();
            pw.Write((byte)Headers.Pause);
            pw.Write(queue.ID);
            Send(pw.GetBytes());
        }

        public int GetOverallProgress()
        {
            int overall = 0;

            foreach (KeyValuePair<int, TransferQueue> pair in _transfer)
            {
                overall += pair.Value.Progress;
            }

            if (overall > 0)
            {
                overall = (overall * 100) / (Transfers.Count * 100);
            }

            return overall;
        }

        public void Send(byte[] data)
        {
            if (Closed)
                return;

            lock (this)
            {
                try
                {
                    _baseSocket.Send(BitConverter.GetBytes(data.Length), 0, 4, SocketFlags.None);
                    _baseSocket.Send(data, 0, data.Length, SocketFlags.None);
                }
                catch
                {
                    Close();
                }
            }
        }

        public void Close()
        {
            Closed = true;
            _baseSocket.Close();
            _transfer.Clear();
            _transfer = null;
            _buffer = null;
            OutputFolder = null;
            
            if (Disconnected != null)
                Disconnected(this, EventArgs.Empty);
        }

        private void process()
        {
            PacketReader pr = new PacketReader(_buffer);

            Headers header = (Headers)pr.ReadByte();

            switch (header)
            {
                case Headers.Queue:
                    {
                        int id = pr.ReadInt32();
                        string fileName = pr.ReadString();
                        long length = pr.ReadInt64();

                        TransferQueue queue = TransferQueue.CreateDownloadQueue(this, id, Path.Combine(OutputFolder, Path.GetFileName(fileName)), length);

                        _transfer.Add(id, queue);

                        if (Queued != null)
                        {
                            Queued(this, queue);
                        }
                    }
                    break;

                case Headers.Start:
                    {
                        int id = pr.ReadInt32();

                        if (_transfer.ContainsKey(id))
                        {
                            _transfer[id].Start();
                        }
                    }
                    break;

                case Headers.Stop:
                    {
                        int id = pr.ReadInt32();

                        if(_transfer.ContainsKey(id))
                        {
                            TransferQueue queue = _transfer[id];

                            queue.Stop();
                            queue.Close();

                            if (Stopped != null)
                                Stopped(this, queue);

                            _transfer.Remove(id);
                        }
                    }
                    break;

                case Headers.Pause:
                    {
                        int id = pr.ReadInt32();

                        if (_transfer.ContainsKey(id))
                        {
                            _transfer[id].Pause();
                        }
                    }
                    break;

                case Headers.Chunk:
                    {
                        int id = pr.ReadInt32();
                        long index = pr.ReadInt64();
                        int size = pr.ReadInt32();
                        byte[] buffer = pr.ReadBytes(size);

                        TransferQueue queue = _transfer[id];

                        queue.Write(buffer, index);

                        queue.Progress = (int)((queue.Transferred * 100) / queue.Lenght);

                        if (queue.LastProgress < queue.Progress)
                        {
                            queue.LastProgress = queue.Progress;

                            if (ProgressChanged != null)
                            {
                                ProgressChanged(this, queue);                                
                            }

                            if (queue.Progress == 100)
                            {
                                queue.Close();

                                if (Complete != null)
                                {
                                    Complete(this, queue);
                                }
                            }
                        }
                    }
                    break;
            }

            pr.Dispose();
        }

        private void receiveCallback(IAsyncResult ar)
        {
            try
            {
                int found = _baseSocket.EndReceive(ar);

                if(found >= 4 )
                {
                    _baseSocket.Receive(_buffer, 0, 4, SocketFlags.None);

                    int size = BitConverter.ToInt32(_buffer, 0);
                    
                    int read = _baseSocket.Receive(_buffer, 0, size, SocketFlags.None);

                    while (read < size)
                    {
                        read += _baseSocket.Receive(_buffer, read, size - read, SocketFlags.None);
                    }

                    process();
                }

                Run();
            }
            catch
            {
                Close();          
            }
        }

        internal void callProgressChanged(TransferQueue queue)
        {
            if (ProgressChanged != null)
            {
                ProgressChanged(this, queue);
            }
        }
    }
}
