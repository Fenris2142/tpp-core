using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using TPP.ArgsParsing.Types;
using TPP.Common;
using TPP.Core.Chat;
using TPP.Persistence.Models;
using TPP.Persistence.Repos;

namespace TPP.Core.Commands.Definitions
{
    public class StopToken
    {
        public bool ShouldStop { get; set; }
    }

    public class OperatorCommands : ICommandCollection
    {
        private readonly StopToken _stopToken;
        private readonly ImmutableHashSet<string> _operatorNamesLower;
        private readonly IBank<User> _pokeyenBank;
        private readonly IBank<User> _tokensBank;
        private readonly IMessageSender _messageSender;
        private readonly IBadgeRepo _badgeRepo;
        private readonly IUserRepo _userRepo;

        public OperatorCommands(
            StopToken stopToken,
            IEnumerable<string> defaultOperatorNames,
            IBank<User> pokeyenBank,
            IBank<User> tokensBank,
            IMessageSender messageSender,
            IBadgeRepo badgeRepo,
            IUserRepo userRepo)
        {
            _stopToken = stopToken;
            _operatorNamesLower = defaultOperatorNames.Select(s => s.ToLowerInvariant()).ToImmutableHashSet();
            _pokeyenBank = pokeyenBank;
            _tokensBank = tokensBank;
            _messageSender = messageSender;
            _badgeRepo = badgeRepo;
            _userRepo = userRepo;
        }

        public IEnumerable<Command> Commands => new[]
        {
            new Command("stopnew", Stop)
            {
                Description = "Operators only: Stop the core, or cancel a previously issued stop command. " +
                              "Argument: cancel(optional)"
            },
            new Command("pokeyenadjust", AdjustPokeyen)
            {
                Aliases = new[] { "adjustpokeyen" },
                Description = "Operators only: Add or remove pokeyen from an user. " +
                              "Arguments: p<amount>(can be negative) <user> <reason>"
            },
            new Command("tokensadjust", AdjustTokens)
            {
                Aliases = new[] { "adjusttokens" },
                Description = "Operators only: Add or remove tokens from an user. " +
                              "Arguments: t<amount>(can be negative) <user> <reason>"
            },
            new Command("transferbadge", TransferBadge)
            {
                Description = "Operators only: Transfer badges from one user to another user. " +
                              "Arguments: <gifter> <recipient> <pokemon> <number of badges>(Optional) <reason>"
            },
            new Command("createbadge", CreateBadge)
            {
                Description = "Operators only: Create a badge for a user. " +
                              "Arguments: <recipient> <pokemon> <number of badges>(Optional)"
            },
            new Command("addrole", AddRole)
            {
                Aliases = new[] { "giverole" },
                Description = "Operators only: Give a user a role." +
                              "Arguments: <user> <role>"
            },
            new Command("removerole", RemoveRole)
            {
                Description = "Operators only: Remove a role from a user." +
                              "Arguments: <user> <role>"
            },
            new Command("showroles", showRoles)
            {
                Aliases = new[] {"roles"},
                Description = "Operators only: Show which roles a user has." +
                              "Arguments: <user>"
            }
        }.Select(cmd => cmd.WithCondition(
            canExecute: ctx => IsOperator(ctx.Message.User),
            ersatzResult: new CommandResult { Response = "Only operators can use that command" }));

        private bool IsOperator(User user) =>
            _operatorNamesLower.Contains(user.SimpleName) || (user.Roles == null ? false : user.Roles.Contains(Role.Operator));

        private Task<CommandResult> Stop(CommandContext context)
        {
            HashSet<string> argSet = context.Args.Select(arg => arg.ToLowerInvariant()).ToHashSet();
            bool cancel = argSet.Remove("cancel");

            if (argSet.Count > 0)
                return Task.FromResult(new CommandResult { Response = "too many arguments" });

            string message = cancel
                ? _stopToken.ShouldStop
                    ? "cancelled a prior stop command (new core)"
                    : "main loop already not stopping (new core)"
                : _stopToken.ShouldStop
                    ? "main loop already stopping (new core)"
                    : "stopping main loop (new core)";
            _stopToken.ShouldStop = !cancel;
            return Task.FromResult(new CommandResult { Response = message });
        }

        public Task<CommandResult> AdjustPokeyen(CommandContext context)
            => AdjustCurrency<SignedPokeyen>(context, _pokeyenBank, "pokeyen");

        public Task<CommandResult> AdjustTokens(CommandContext context)
            => AdjustCurrency<SignedTokens>(context, _tokensBank, "token");

        private async Task<CommandResult> AdjustCurrency<T>(
            CommandContext context, IBank<User> bank, string currencyName) where T : ImplicitNumber
        {
            (User user, T deltaObj, ManyOf<string> reasonParts) =
                await context.ParseArgs<AnyOrder<User, T, ManyOf<string>>>();
            string reason = string.Join(' ', reasonParts.Values);
            int delta = deltaObj;

            var additionalData = new Dictionary<string, object?> { ["responsible_user"] = context.Message.User.Id };
            await bank.PerformTransaction(new Transaction<User>(
                user, delta, TransactionType.ManualAdjustment, additionalData));

            bool isSelf = user == context.Message.User;
            if (isSelf)
            {
                return new CommandResult
                {
                    Response = $"Your {currencyName} balance was adjusted by {delta:+#;-#}. Reason: {reason}"
                };
            }
            else
            {
                if (string.IsNullOrEmpty(reason))
                {
                    return new CommandResult { Response = $"Must provide a reason for the {currencyName} adjustment" };
                }
                await _messageSender.SendWhisper(user,
                    $"{context.Message.User.Name} adjusted your {currencyName} balance by {delta:+#;-#}. Reason: {reason}");
                return new CommandResult
                {
                    Response = $"{user.Name}'s {currencyName} balance was adjusted by {delta:+#;-#}. Reason: {reason}"
                };
            }
        }

        public async Task<CommandResult> TransferBadge(CommandContext context)
        {
            (User gifter, (User recipient, PkmnSpecies species, Optional<PositiveInt> amountOpt),
                    ManyOf<string> reasonParts) =
                await context.ParseArgs<User, AnyOrder<User, PkmnSpecies, Optional<PositiveInt>>, ManyOf<string>>();
            string reason = string.Join(' ', reasonParts.Values);
            int amount = amountOpt.Map(i => i.Number).OrElse(1);

            if (string.IsNullOrEmpty(reason))
                return new CommandResult { Response = "Must provide a reason" };

            if (gifter == context.Message.User)
                return new CommandResult { Response = "Use the regular gift command if you're the gifter" };

            if (recipient == gifter)
                return new CommandResult { Response = "Gifter cannot be equal to recipient" };

            List<Badge> badges = await _badgeRepo.FindByUserAndSpecies(gifter.Id, species);
            if (badges.Count < amount)
                return new CommandResult
                {
                    Response =
                        $"You tried to transfer {amount} {species} badges, but the gifter only has {badges.Count}."
                };

            IImmutableList<Badge> badgesToGift = badges.Take(amount).ToImmutableList();
            var data = new Dictionary<string, object?>
            {
                ["gifter"] = gifter.Id,
                ["responsible_user"] = context.Message.User.Id,
                ["reason"] = reason
            };
            await _badgeRepo.TransferBadges(badgesToGift, recipient.Id, BadgeLogType.TransferGiftRemote, data);

            await _messageSender.SendWhisper(recipient, amount > 1
                ? $"{context.Message.User.Name} transferred {amount} {species} badges from {gifter.Name} to you. Reason: {reason}"
                : $"{context.Message.User.Name} transferred a {species} badge from {gifter.Name} to you. Reason: {reason}");
            return new CommandResult
            {
                Response = amount > 1
                    ? $"transferred {amount} {species} badges from {gifter.Name} to {recipient.Name}. Reason: {reason}"
                    : $"transferred a {species} badge from {gifter.Name} to {recipient.Name}. Reason: {reason}",
                ResponseTarget = ResponseTarget.Chat
            };
        }

        public async Task<CommandResult> CreateBadge(CommandContext context)
        {
            (User recipient, PkmnSpecies species, Optional<PositiveInt> amountOpt) =
                await context.ParseArgs<AnyOrder<User, PkmnSpecies, Optional<PositiveInt>>>();
            int amount = amountOpt.Map(i => i.Number).OrElse(1);

            for (int i = 0; i < amount; i++)
                await _badgeRepo.AddBadge(recipient.Id, species, Badge.BadgeSource.ManualCreation);

            return new CommandResult
            {
                Response = amount > 1
                    ? $"{amount} badges of species {species} created for user {recipient.Name}."
                    : $"Badge of species {species} created for user {recipient.Name}."
            };
        }

        public async Task<CommandResult> AddRole(CommandContext context)
        {
            (User user, Role role) = await context.ParseArgs<User, Role>();
            string response;

            if (user.Roles != null)
            {
                if (user.Roles.Contains(role))
                {
                    response = string.Format("{0} already has the role {1}", user.SimpleName, role.ToString());
                }
                else
                {
                    List<Role> newGroup = user.Roles;
                    newGroup.Add(role);
                    await _userRepo.SetUserRoles(user, newGroup);
                    response = string.Format("{0} now has the roles: {1}", user.SimpleName, string.Join(", ", newGroup));
                }
            }
            else
            {
                await _userRepo.SetUserRoles(user, new List<Role> { role });
                response = string.Format("{0} now has the role: {1}", user.SimpleName, role.ToString());
            }

            return new CommandResult
            {
                Response = response
            };
        }

        public async Task<CommandResult> RemoveRole(CommandContext context)
        {
            (User user, Role role) = await context.ParseArgs<User, Role>();
            string response;

            if (user.Roles != null)
            {
                List<Role>? groups = user.Roles;
                groups.Remove(role);
                groups = groups.Count > 0 ? groups : null;
                await _userRepo.SetUserRoles(user, groups);

                response = groups != null ? string.Format("{0} now has the roles: {1}", user.SimpleName, string.Join(", ", groups)) : string.Format("{0} now has no roles", user.SimpleName);
            }
            else
            {
                response = string.Format("{0} already has no roles", user.SimpleName);
            }

            return new CommandResult
            {
                Response = response
            };
        }
        public async Task<CommandResult> showRoles(CommandContext context)
        {
            User user = await context.ParseArgs<User>();
            string response = user.Roles == null ? string.Format("{0} has no roles", user.SimpleName) : string.Format("{0} has the roles: {1}", user.SimpleName, string.Join(", ", user.Roles));

            return new CommandResult
            {
                Response = response
            };
        }
    }
}
