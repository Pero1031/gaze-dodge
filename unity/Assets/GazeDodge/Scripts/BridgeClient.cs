using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GazeDodge
{
    /// <summary>
    /// tobii_bridge.py と WebSocket（ws://127.0.0.1:8765）で通信する。メッセージの種類は tobii_bridge.py の先頭のコメントを参照。
    /// 受信は別スレッドで行い、届いた時刻（Clock.Ms）を付けてためておく。Pump() をメインスレッドで毎フレーム呼ぶと、
    /// 視線サンプルは OnGaze に、要求への返事は Request() の Task に渡る。
    /// </summary>
    public class BridgeClient : IDisposable
    {
        public JObject Info { get; private set; }            // hello の内容（mode, model, freq）
        public bool IsOpen => ws != null && ws.State == WebSocketState.Open && Info != null;

        /// <summary>視線サンプル（type == "gaze"）と、その到着時刻 [ms]</summary>
        public Action<JObject, double> OnGaze;
        /// <summary>接続が切れたとき（メインスレッドで呼ばれる）</summary>
        public Action OnClosed;

        ClientWebSocket ws;
        CancellationTokenSource cts;
        readonly ConcurrentQueue<(double t, string json)> inbox = new ConcurrentQueue<(double, string)>();
        readonly List<(string type, TaskCompletionSource<JObject> tcs)> waiters = new List<(string, TaskCompletionSource<JObject>)>();
        readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        TaskCompletionSource<JObject> helloTcs;
        int closedFlag;   // 受信スレッドが切断を検知したら 1

        public async Task<JObject> Connect(int port, int timeoutMs = 3000)
        {
            if (IsOpen) return Info;
            Close();
            ws = new ClientWebSocket();
            cts = new CancellationTokenSource();
            helloTcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref closedFlag, 0);
            var sock = ws;
            using (var timeout = new CancellationTokenSource(timeoutMs))
            {
                try
                {
                    await sock.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), timeout.Token);
                }
                catch (Exception)
                {
                    Close();
                    throw new Exception("ブリッジに接続できません");
                }
                var token = cts.Token;
                _ = Task.Run(() => ReceiveLoop(sock, token));
                // hello は Pump()（毎フレーム）が受け取る
                var hello = helloTcs.Task;
                var done = await Task.WhenAny(hello, Task.Delay(timeoutMs));
                if (done != hello || hello.Status != TaskStatus.RanToCompletion)
                {
                    Close();
                    throw new Exception(done != hello ? "timeout" : "ブリッジに接続できません");
                }
                return hello.Result;
            }
        }

        async Task ReceiveLoop(ClientWebSocket sock, CancellationToken ct)
        {
            var buf = new byte[16384];
            var ms = new MemoryStream();
            try
            {
                while (sock.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var r = await sock.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        // ブリッジが閉じた（終了など）。こちらからも閉じる合図を返して、切断を正しく終える
                        await sock.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false);
                        break;
                    }
                    ms.Write(buf, 0, r.Count);
                    if (r.EndOfMessage)
                    {
                        inbox.Enqueue((Clock.Ms, Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length)));
                        ms.SetLength(0);
                    }
                }
            }
            catch (Exception) { /* 切断・終了 */ }
            if (sock == ws) Interlocked.Exchange(ref closedFlag, 1);
        }

        /// <summary>メインスレッドで毎フレーム呼ぶ。</summary>
        public void Pump()
        {
            while (inbox.TryDequeue(out var item))
            {
                JObject m;
                try { m = JObject.Parse(item.json); }
                catch (Exception) { continue; }
                var type = (string)m["type"];
                if (type == "hello")
                {
                    Info = m;
                    helloTcs?.TrySetResult(m);
                    continue;
                }
                if (type == "gaze")
                {
                    OnGaze?.Invoke(m, item.t);
                    continue;
                }
                // index.html と同じく、返事の種類が一致する（または error の）最初の待ち手に渡す
                int i = waiters.FindIndex(w => w.type == type || type == "error");
                if (i >= 0)
                {
                    var w = waiters[i];
                    waiters.RemoveAt(i);
                    if (type == "error") w.tcs.TrySetException(new Exception((string)m["message"]));
                    else w.tcs.TrySetResult(m);
                }
            }
            if (Interlocked.Exchange(ref closedFlag, 0) == 1)
            {
                Info = null;
                FailWaiters("ブリッジとの接続が切れました");
                OnClosed?.Invoke();
            }
        }

        /// <summary>メッセージを送る。replyType を指定すると、その種類の返事が届くまで待つ。</summary>
        public Task<JObject> Request(object msg, string replyType = null)
        {
            TaskCompletionSource<JObject> tcs = null;
            if (replyType != null)
            {
                tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
                waiters.Add((replyType, tcs));
            }
            var send = Send(JsonConvert.SerializeObject(msg));
            if (tcs == null) return send.ContinueWith(_ => (JObject)null, TaskScheduler.Default);
            send.ContinueWith(t =>
            {
                if (t.IsFaulted) tcs.TrySetException(new Exception("ブリッジに送信できません"));
            }, TaskScheduler.Default);
            return tcs.Task;
        }

        async Task Send(string json)
        {
            var sock = ws;
            if (sock == null || sock.State != WebSocketState.Open) throw new Exception("ブリッジに接続していません");
            var bytes = Encoding.UTF8.GetBytes(json);
            await sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await sock.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
                          .ConfigureAwait(false);
            }
            finally { sendLock.Release(); }
        }

        void FailWaiters(string message)
        {
            foreach (var w in waiters) w.tcs.TrySetException(new Exception(message));
            waiters.Clear();
            helloTcs?.TrySetCanceled();   // Connect は自分で待ち時間を見ているので、ここでは取り消すだけ
        }

        public void Close()
        {
            // 先に手放しておく（受信スレッドが「切れた」と知らせないように）
            var sock = ws;
            var c = cts;
            ws = null;
            cts = null;
            Info = null;
            // つながっていれば閉じる合図を送ってから切る（ブリッジ側に「切断のエラー」を出させない）。長くは待たない
            if (sock != null && sock.State == WebSocketState.Open)
            {
                try
                {
                    Task.Run(() => sock.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)).Wait(300);
                }
                catch (Exception) { }
            }
            try { c?.Cancel(); } catch (Exception) { }
            try { sock?.Abort(); sock?.Dispose(); } catch (Exception) { }
            FailWaiters("ブリッジとの接続を閉じました");
        }

        public void Dispose() => Close();
    }
}
