﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Playground.BalancingTokenParse
{
    class FirstFollowCalculator : IFirstFollowProvider
    {
        public FirstFollowCalculator(IReadOnlyCollection<Rule> rules)
        {
            // based on https://en.wikipedia.org/wiki/LL_parser

            var allSymbols = rules.SelectMany(r => new[] { r.Produced }.Concat(r.Symbols))
                .Distinct()
                .ToArray();
            var startSymbol = allSymbols.OfType<NonTerminal>().Single(s => !rules.Any(r => r.Produced != s && r.Symbols.Contains(s)));

            // first computation
            var firsts = allSymbols.ToDictionary(
                s => s, 
                s => (ISet<Token>)(s is Token ? new HashSet<Token> { (Token)s } : new HashSet<Token>())
            );

            bool changed;
            do
            {
                changed = false;
                foreach (var rule in rules)
                {
                    // for each symbol, add first(symbol) - null to first(produced)
                    // until we hit a non-nullable symbol
                    var nullable = true;
                    foreach (var symbol in rule.Symbols)
                    {
                        foreach (var token in firsts[symbol])
                        {
                            if (token != null)
                            {
                                changed |= firsts[rule.Produced].Add(token);
                            }
                        }
                        if (!firsts[symbol].Contains(null))
                        {
                            nullable = false;
                            break;
                        }
                    }

                    // if all symbols were nullable, then produced is nullable
                    if (nullable)
                    {
                        changed |= firsts[rule.Produced].Add(null);
                    }
                }
            } while (changed);

            // follow computation
            var follows = allSymbols.ToDictionary(
                s => s,
                _ => (ISet<Token>)new HashSet<Token>()
            );
            follows[startSymbol].Add(Token.Eof);

            do
            {
                changed = false;
                foreach (var rule in rules)
                {
                    // going backwards reduces the iterations because we learn from the next symbol
                    for (var i = rule.Symbols.Count - 1; i >= 0; --i)
                    {
                        // for the last symbol, give it the follow of the produced symbol
                        if (i == rule.Symbols.Count - 1)
                        {
                            foreach (var token in follows[rule.Produced])
                            {
                                changed |= follows[rule.Symbols[i]].Add(token);
                            }
                        }
                        else
                        {
                            foreach (var token in firsts[rule.Symbols[i + 1]])
                            {
                                if (token != null)
                                {
                                    // add the firsts of the next symbol
                                    changed |= follows[rule.Symbols[i]].Add(token);
                                }
                                else
                                {
                                    // if the next symbol is nullable, also add its follows
                                    foreach (var followToken in follows[rule.Symbols[i + 1]])
                                    {
                                        changed |= follows[rule.Symbols[i]].Add(followToken);
                                    }
                                }
                            }
                        }
                    }
                }
            } while (changed);

            this.StartSymbol = startSymbol;
            this.Tokens = ImmutableHashSet<Token>.Empty.Union(allSymbols.OfType<Token>());
            this.NonTerminals = ImmutableHashSet<NonTerminal>.Empty.Union(allSymbols.OfType<NonTerminal>());
            this.First = firsts.ToDictionary(kvp => kvp.Key, kvp => (IImmutableSet<Token>)ImmutableHashSet.CreateRange(kvp.Value));
            this.Follow = follows.ToDictionary(kvp => kvp.Key, kvp => (IImmutableSet<Token>)ImmutableHashSet.CreateRange(kvp.Value));
        }

        public NonTerminal StartSymbol { get; }
        public IImmutableSet<Token> Tokens { get; }
        public IImmutableSet<NonTerminal> NonTerminals { get; }
        public IReadOnlyDictionary<Symbol, IImmutableSet<Token>> First { get; }
        public IReadOnlyDictionary<Symbol, IImmutableSet<Token>> Follow { get; }

        public IImmutableSet<Token> FirstOf(Symbol symbol) => this.First[symbol];
        public IImmutableSet<Token> FollowOf(Symbol symbol) => this.Follow[symbol];
        public IImmutableSet<Token> FollowOf(Rule rule) => this.FollowOf(rule.Produced);
    }
}
