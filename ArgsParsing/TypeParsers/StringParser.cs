﻿using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;

namespace ArgsParsing.TypeParsers
{
    /// <summary>
    /// A parser that just forwards one argument as a string.
    /// Parsing always succeeds, given that the arguments aren't exhausted.
    /// </summary>
    public class StringParser : BaseArgumentParser<string>
    {
        public override Task<ArgsParseResult<string>> Parse(IImmutableList<string> args, Type[] genericTypes)
        {
            var result = ArgsParseResult<string>.Success(args[0], args.Skip(1).ToImmutableList());
            return Task.FromResult(result);
        }
    }
}
