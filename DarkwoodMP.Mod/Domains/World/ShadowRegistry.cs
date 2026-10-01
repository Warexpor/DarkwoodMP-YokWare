using System.Collections.Generic;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Host: night shadow creatures by network id. Ids are minted here and live for the session.
    /// </summary>
    internal sealed class ShadowRegistry
    {
        private const short MaxId = 9999;
        private readonly Dictionary<short, ShadowCreature> _tracked = new Dictionary<short, ShadowCreature>();
        private short _nextId;

        internal Dictionary<short, ShadowCreature> Tracked => _tracked;

        internal short MintId()
        {
            _nextId++;
            if (_nextId >= MaxId) _nextId = 1;
            return _nextId;
        }

        internal void Register(short id, ShadowCreature sc) => _tracked[id] = sc;

        internal bool TryRemove(short id, out ShadowCreature sc)
        {
            if (!_tracked.TryGetValue(id, out sc))
                return false;
            _tracked.Remove(id);
            return true;
        }

        internal void Reset()
        {
            _tracked.Clear();
            _nextId = 0;
        }
    }
}
