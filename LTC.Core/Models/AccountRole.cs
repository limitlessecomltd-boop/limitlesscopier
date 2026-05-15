namespace LTC.Core.Models;

/// <summary>
/// The role an account plays in the copy network.
/// An account can be a master (signal source), a slave (follower), or both.
/// </summary>
public enum AccountRole
{
    Master = 1,
    Slave = 2,
    Both = 3
}
