using Microsoft.AspNetCore.SignalR;
using TempMail.Infrastructure;
using TempMail.Web.Security;
namespace TempMail.Web;
public sealed class MailHub(MailboxSession sessions, MailboxService mailboxes) : Hub
{
    public async Task JoinMailbox()
    {
        try
        {
            var box = await mailboxes.RequireAsync(sessions.Require(Context.GetHttpContext()!), Context.ConnectionAborted);
            await Groups.AddToGroupAsync(Context.ConnectionId, "mailbox:" + box.PublicId, Context.ConnectionAborted);
        }
        catch (TempMail.Application.MailPolicyException) { throw new HubException("Mailbox unavailable."); }
    }
}
