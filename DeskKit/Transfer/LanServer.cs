using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace DeskKit.Transfer
{
    /// <summary>
    /// TCP 监听器：accept 循环 + 端口占用自动 +1 重试（最多 5 次）（2026-11-10）。
    /// 每个客户端连接丢给线程池跑 onClient（内部自含 HTTP 解析与响应），主程序零阻塞。
    /// 注意：首次 listener.Start() Windows 防火墙会弹授权框，拒绝则外部设备连不上。
    /// </summary>
    public sealed class LanServer
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        public int ActualPort { get; private set; }

        /// <summary>启动。按候选顺序试端口（2026-11-11：候选列表由调用方给，自动模式 80→8080→52123..）。
        /// 全部失败返回 false（写日志由调用方负责）。</summary>
        public bool Start(int[] candidatePorts, Action<TcpClient> onClient)
        {
            Stop(); // 防重复启动

            TcpListener? bound = null;
            foreach (var p in candidatePorts)
            {
                try
                {
                    bound = new TcpListener(IPAddress.Any, p);
                    bound.Start();
                    ActualPort = p;
                    break;
                }
                catch (SocketException)
                {
                    bound = null; // 端口被占，试下一个
                }
            }
            if (bound == null) return false;

            _listener = bound;
            _running = true;
            _acceptThread = new Thread(() => AcceptLoop(onClient))
            {
                IsBackground = true,
                Name = "DeskKit.LanServer.Accept",
            };
            _acceptThread.Start();
            return true;
        }

        private void AcceptLoop(Action<TcpClient> onClient)
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener!.AcceptTcpClient();
                }
                catch (SocketException) when (!_running)
                {
                    break; // Stop() 引发的正常中断
                }
                catch (Exception)
                {
                    if (!_running) break;
                    Thread.Sleep(100); // 偶发 accept 错误，别转成 CPU 空转
                    continue;
                }
                // 每连接独立处理，异常绝不能炸 accept 循环
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { onClient(client); }
                    catch { try { client.Close(); } catch { } }
                });
            }
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }
    }
}
