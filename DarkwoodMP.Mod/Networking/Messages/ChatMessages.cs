namespace DWMPHorde.Networking
{
    /// <summary>Yokyy product: reliable chat line over Horde wire.</summary>
    public struct ChatMessagePayload
    {
        public int SenderId;
        public string SenderName;
        public string Message;

        public void Serialize(NetWriter w)
        {
            w.Put(SenderId);
            w.Put(SenderName ?? "");
            w.Put(Message ?? "");
        }

        public static ChatMessagePayload Deserialize(NetReader r)
        {
            return new ChatMessagePayload
            {
                SenderId = r.GetInt(),
                SenderName = r.GetString(),
                Message = r.GetString()
            };
        }
    }
}
