using System.Collections.Generic;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Who triggered the GameEvents about to fire on the host. Postfix stamps
    /// this into GameEventsFiredMessage.ActorPlayerId so peers only run
    /// Player.Instance personal effects for that actor.
    /// </summary>
    public static class GeFireActorContext
    {
        private static readonly Stack<int> _stack = new Stack<int>(4);

        public static int Depth => _stack.Count;

        public static void Push(int actorPlayerId)
        {
            if (actorPlayerId > 0)
                _stack.Push(actorPlayerId);
        }

        public static void Pop()
        {
            if (_stack.Count > 0)
                _stack.Pop();
        }

        public static int PeekOr(int fallback)
            => _stack.Count > 0 ? _stack.Peek() : fallback;

        public static void Reset() => _stack.Clear();
    }
}
