using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal sealed class PluginAccessState
    {
        internal string Owner, Account, Home, Client, ClientHome, ClientAccount, Plugin, Revision;
        internal TunnelTool[] Tools;
        internal HashSet<string> Selected;
        internal TunnelGrant[] ExistingRules;
    }

    // A plugin exports actions, not necessarily enumerable resources. Its grants
    // are independent of both the resource picker and manually scoped rules.
    internal sealed class PluginAccess
    {
        private readonly RelayStore store;
        private readonly ToolTunnel tunnel;
        internal PluginAccess(RelayStore store, ToolTunnel transport = null)
        { this.store = store; tunnel = transport ?? new ToolTunnel(store); }

        internal static string Key(TunnelTool tool) { return tool.Server + "/" + tool.Name; }
        internal static string Label(TunnelTool tool)
        {
            string title = String.IsNullOrWhiteSpace(tool.Title) ? Json.Str(Json.Get(tool.Annotations, "title")) : tool.Title;
            if (String.IsNullOrWhiteSpace(title)) return tool.Name; // Do not invent provider semantics.
            return title.Substring(0, Math.Min(200, title.Length));
        }
        internal static bool Managed(TunnelGrant grant, string owner, string client, string plugin)
        { return grant.Instance == owner && grant.CatalogClient == client && grant.CatalogPlugin == plugin; }
        private string Revision(string owner, string client, string plugin)
        { return ToolTunnel.Hash(Json.Read<object>(Json.Write(tunnel.Grants().Where(g => Managed(g, owner, client, plugin)).OrderBy(g => g.Id).ToArray()))); }

        internal async Task<PluginAccessState> Load(string owner, string account, string home, string client, string clientHome, string clientAccount, string plugin, CancellationToken token)
        {
            var identity = tunnel.Owner(owner);
            if (identity.Account != account || !RelayStore.SamePath(identity.Home, home)) throw new InvalidOperationException("Le compte de cette instance a changé. Rouvrez sa configuration.");
            var tools = (await tunnel.Discover(owner, token).ConfigureAwait(false)).Where(t => InstanceResources.PluginKey(t) == plugin).ToArray();
            ToolTunnel.CheckOwner(identity,tunnel.Owner(owner));
            if (tools.Length == 0) throw new InvalidOperationException("Ce plugin n’est plus disponible dans cette instance. Actualisez ses ressources.");
            var grants = tunnel.Grants();
            var selected = new HashSet<string>(grants.Where(g => Managed(g, owner, client, plugin) && g.Enabled && g.Account == account && RelayStore.SamePath(g.Home, home))
                .Where(g => g.Sources.Any(s => RelayStore.SamePath(s.Key, clientHome) && s.Value == clientAccount)).SelectMany(g => g.Tools).Select(Key));
            var existing = grants.Where(g => g.Instance == owner && g.Enabled && !Managed(g, owner, client, plugin) && g.Tools.Any(t => InstanceResources.PluginKey(t) == plugin)
                && g.Sources.Any(s => RelayStore.SamePath(s.Key, clientHome) && s.Value == clientAccount)).ToArray();
            return new PluginAccessState { Owner = owner, Account = account, Home = home, Client = client, ClientHome = clientHome, ClientAccount = clientAccount, Plugin = plugin,
                Tools = tools, Selected = selected, ExistingRules = existing, Revision = ToolTunnel.Hash(Json.Read<object>(Json.Write(grants.Where(g=>Managed(g,owner,client,plugin)).OrderBy(g=>g.Id).ToArray()))) };
        }

        internal async Task Save(PluginAccessState state, IEnumerable<string> selection, CancellationToken token)
        {
            var selected = new HashSet<string>(selection);
            TunnelGrant replacement = null;
            if (selected.Count > 0)
            {
                var owner = tunnel.Owner(state.Owner);
                if (owner.Account != state.Account || !RelayStore.SamePath(owner.Home, state.Home)) throw new InvalidOperationException("Le compte de cette instance a changé.");
                var live = await tunnel.Discover(state.Owner, token).ConfigureAwait(false);
                var chosen = new List<TunnelTool>();
                foreach (string key in selected)
                {
                    var shown = state.Tools.SingleOrDefault(t => Key(t) == key && InstanceResources.PluginKey(t) == state.Plugin);
                    var actual = live.SingleOrDefault(t => Key(t) == key && InstanceResources.PluginKey(t) == state.Plugin);
                    if (shown == null || actual == null || ToolTunnel.Signature(shown) != ToolTunnel.Signature(actual)) throw new InvalidOperationException("Les actions du plugin ont changé. Rouvrez sa configuration avant d’enregistrer.");
                    if (ToolTunnel.Limitation(actual).Length > 0) throw new InvalidOperationException(ToolTunnel.Limitation(actual));
                    chosen.Add(actual);
                }
                string name = chosen[0].Group + " via " + owner.Name;
                replacement = new TunnelGrant { Id = Guid.NewGuid().ToString("N"), Name = name.Substring(0, Math.Min(name.Length, 120)), Instance = owner.Id,
                    CatalogClient = state.Client, CatalogPlugin = state.Plugin, Enabled = true, Tools = chosen, Sources = new Dictionary<string, string> { { state.ClientHome, state.ClientAccount } } };
                tunnel.PrepareGrant(replacement);
                ToolTunnel.CheckOwner(owner, tunnel.Owner(state.Owner));
            }
            token.ThrowIfCancellationRequested();
            using (store.Lease("tool-grants"))
            {
                if (Revision(state.Owner, state.Client, state.Plugin) != state.Revision) throw new InvalidOperationException("Ces accès ont été modifiés ailleurs. Rouvrez le plugin pour récupérer les changements.");
                var all = tunnel.Grants();
                all.RemoveAll(g => Managed(g, state.Owner, state.Client, state.Plugin));
                if (replacement != null) all.Add(replacement);
                tunnel.WriteGrants(all);
            }
        }
    }
}
