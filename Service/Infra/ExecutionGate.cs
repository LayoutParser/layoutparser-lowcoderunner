using System;
using System.Threading;
using System.Threading.Tasks;

namespace LayoutParserLowCodeRunner.Service.Infra
{
    /// <summary>Erro de negócio do serviço já traduzido para HTTP (status + exitCode + mensagem segura).</summary>
    internal sealed class ServiceException : Exception
    {
        public int Status { get; }
        public int ExitCode { get; }
        public int? RetryAfterSeconds { get; }

        public ServiceException(int status, int exitCode, string message, int? retryAfterSeconds = null)
            : base(message)
        {
            Status = status;
            ExitCode = exitCode;
            RetryAfterSeconds = retryAfterSeconds;
        }
    }

    /// <summary>
    /// Slots de execução (MaxConcurrentRunners) + fila limitada. Quem desiste (token cancelado) NUNCA segura
    /// slot: a espera é cancelável e a liberação acontece no Dispose do ticket.
    /// </summary>
    internal sealed class ExecutionGate
    {
        private readonly SemaphoreSlim _slots;
        private readonly int _maxQueue;
        private readonly int _retryAfter;
        private int _running;
        private int _queued;

        public ExecutionGate(int slots, int maxQueue, int retryAfterSeconds)
        {
            _slots = new SemaphoreSlim(slots, slots);
            _maxQueue = maxQueue;
            _retryAfter = retryAfterSeconds;
        }

        public int Running { get { return Volatile.Read(ref _running); } }
        public int Queued { get { return Volatile.Read(ref _queued); } }

        /// <param name="enforceQueueLimit">Requisição única: fila cheia => 503. O batch já é limitado por MaxCandidates.</param>
        public async Task<IDisposable> AcquireAsync(CancellationToken ct, bool enforceQueueLimit)
        {
            if (!_slots.Wait(0))
            {
                var q = Interlocked.Increment(ref _queued);
                try
                {
                    if (enforceQueueLimit && q > _maxQueue)
                        throw new ServiceException(503, 0, "Fila de execucao cheia; tente novamente.", _retryAfter);
                    await _slots.WaitAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _queued);
                }
            }

            Interlocked.Increment(ref _running);
            return new Ticket(this);
        }

        private sealed class Ticket : IDisposable
        {
            private ExecutionGate _gate;
            public Ticket(ExecutionGate g) { _gate = g; }
            public void Dispose()
            {
                var g = Interlocked.Exchange(ref _gate, null);
                if (g == null) return;
                Interlocked.Decrement(ref g._running);
                g._slots.Release();
            }
        }
    }
}
