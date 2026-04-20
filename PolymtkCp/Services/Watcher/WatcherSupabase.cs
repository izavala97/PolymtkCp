namespace PolymtkCp.Services.Watcher;

/// <summary>
/// Hosts a long-lived Supabase client constructed with the SERVICE ROLE key.
/// This bypasses RLS — only the background watcher should use it. HTTP-request
/// code paths must keep using the per-request <see cref="Supabase.Client"/>
/// from DI (which carries the user's JWT).
/// </summary>
public sealed class WatcherSupabase
{
    public Supabase.Client Client { get; }

    public WatcherSupabase(Supabase.Client client)
    {
        Client = client;
    }
}
