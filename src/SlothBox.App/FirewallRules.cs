using SlothBox.Core;

namespace SlothBox.App;

// Spec 7.4.6: whether Windows Firewall has an enabled inbound rule under this display name that allows traffic (the
// README's rule for the phone page). SlothBox only reads the rules; it never changes them.
internal sealed class FirewallRules : IFirewallRules
{
    private const int Inbound = 1; // NET_FW_RULE_DIR_IN
    private const int Allow = 1;   // NET_FW_ACTION_ALLOW

    public bool Exists(string displayName)
    {
        if (Type.GetTypeFromProgID("HNetCfg.FwPolicy2") is not { } policyType)
            return false;
        try
        {
            dynamic policy = Activator.CreateInstance(policyType)!;
            foreach (dynamic rule in policy.Rules)
                if (string.Equals((string)rule.Name, displayName, StringComparison.OrdinalIgnoreCase) &&
                    (int)rule.Direction == Inbound && (int)rule.Action == Allow && (bool)rule.Enabled)
                    return true;
            return false;
        }
        catch (Exception)
        {
            // The rules could not be read: the phone block shows its hint, which is only a reminder.
            return false;
        }
    }
}
