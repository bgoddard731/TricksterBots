using System;
using System.Collections.Generic;
using System.Linq;
using Trickster.cloud;

namespace Trickster.Bots
{
    public class WhistBot : BaseBot<WhistOptions>
    {
        public WhistBot(WhistOptions options, Suit trumpSuit) : base(options, trumpSuit)
        {
        }

        protected override Card TryLeadTowardPartnerIntroducedSuit(PlayerBase player, IReadOnlyList<Card> legalCards, IReadOnlyList<Card> cardsPlayed,
            PlayersCollectionBase players, bool isDefending, IReadOnlyList<Card> bossCards, string cardsPlayedInOrder = null)
        {
            //  come back in partner's suit (void signal and/or auction) before cashing a boss elsewhere
            if (isDefending)
            {
                return null;
            }

            //  If we already have enough "boss" cards left to make our team's bid,
            //  just let them play out rather than trying to come back in partner's suit.
            var declarer = players.FirstOrDefault(p => new WhistBid(p.Bid).IsDeclareBid);
            if (declarer != null)
            {
                var contract = new WhistBid(declarer.Bid);
                var partner = players.PartnerOf(declarer);
                var tricksTaken = declarer.CardsTaken.Length / 8;
                if (partner != null)
                    tricksTaken += partner.CardsTaken.Length / 8;

                if (tricksTaken + bossCards.Count >= contract.Tricks)
                    return null;
            }

            var partnerSuit = PartnerIntroducedSuitFromAuctionAndSignal(player, players, cardsPlayed, cardsPlayedInOrder);
            if (partnerSuit == Suit.Unknown || !legalCards.Any(c => EffectiveSuit(c) == partnerSuit))
                return null;

            var loserInPartnerSuit = legalCards.Where(c => EffectiveSuit(c) == partnerSuit && !IsCardHigh(c, cardsPlayed)).OrderBy(RankSort).FirstOrDefault();
            if (loserInPartnerSuit != null)
                return loserInPartnerSuit;
            if (bossCards.Any(c => EffectiveSuit(c) == partnerSuit))
                return bossCards.First(c => EffectiveSuit(c) == partnerSuit);

            return null;
        }

        private Suit PartnerIntroducedSuitFromAuctionAndSignal(PlayerBase player, PlayersCollectionBase players, IReadOnlyList<Card> cardsPlayed,
            string cardsPlayedInOrder)
        {
            var partner = players.PartnersOf(player).FirstOrDefault();
            if (partner == null)
                return Suit.Unknown;

            var suit = partner.GoodSuit;
            if (suit != Suit.Unknown && suit != trump)
                return suit;


            if (trump == Suit.Unknown)
            {
                //  Infer a suit partner is promoting: walk completed tricks (newest first) and find
                //  one where partner led and their lead was not the highest card of the lead suit in that trick.
                var playedOrder = cardsPlayedInOrder;
                if (!string.IsNullOrEmpty(playedOrder))
                {
                    var ordered = GetCardsPlayedInOrder(playedOrder);
                    if (ordered.Count > 0 && ordered.All(sc => sc.seat >= 0 && sc.seat < players.Count))
                    {
                        var tricks = GetCardsPlayedByTrick(playedOrder, players.Count);
                        for (var t = tricks.Count - 1; t >= 0; t--)
                        {
                            var trick = tricks[t];
                            if (trick.Count == 0 || trick[0].seat != partner.Seat)
                                continue;

                            var leadCard = trick[0].card;
                            var leadSuit = EffectiveSuit(leadCard);
                            if (leadSuit == Suit.Unknown || leadSuit == Suit.Joker)
                                continue;

                            var maxRankInLeadSuit = trick
                                .Where(sc => EffectiveSuit(sc.card) == leadSuit)
                                .Max(sc => RankSort(sc.card));

                            if (RankSort(leadCard) < maxRankInLeadSuit)
                                return leadSuit;
                        }
                    }
                }
            }

            return Suit.Unknown;
        }

        //  NT slough helper with some logic based on base bot's LowestCardFromWeakestSuit: pick a low discard from a weak suit.
        private Card LowestCardFromWeakestSuitNT(IReadOnlyList<Card> legalCards, IReadOnlyList<Card> cardsPlayed)
        {
            var cards = legalCards as IList<Card> ?? legalCards.ToList();

            var suitCounts = cards.GroupBy(EffectiveSuit).Select(g => new { suit = g.Key, count = g.Count() }).ToList();

            //  try to ditch a singleton that's not "boss" and whose suit has the most outstanding cards
            var bestSingletonSuitCount = suitCounts.Where(sc => sc.count == 1)
                .Where(sc => !IsCardHigh(cards.Single(c => EffectiveSuit(c) == sc.suit), cardsPlayed))
                .OrderBy(sc => cardsPlayed.Count(c => EffectiveSuit(c) == sc.suit)).FirstOrDefault();

            if (bestSingletonSuitCount != null)
                return cards.Single(c => EffectiveSuit(c) == bestSingletonSuitCount.suit);

            //  now we look at doubletons in the order of the number of remaining cards in the suit
            var doubletonSuitCounts = suitCounts.Where(sc => sc.count == 2).OrderBy(sc => cardsPlayed.Count(c => EffectiveSuit(c) == sc.suit)).ToList();

            foreach (var sc in doubletonSuitCounts)
            {
                var suitCards = cards.Where(c => EffectiveSuit(c) == sc.suit).OrderBy(RankSort).ToList();


                if (IsCardHigh(suitCards[1], cardsPlayed) && !AreCardsEquivalent(suitCards[0], suitCards[1], cardsPlayed))
                    return suitCards[0];

                if (!suitCards.Any(c => HasSoleUnplayedCardStrictlyAbove(c, cardsPlayed)))
                    return suitCards[0];
            }

            //  return the lowest card from the longest suit
            return cards.OrderByDescending(c => cards.Count(c1 => EffectiveSuit(c1) == c.suit)).ThenBy(RankSort).First();
        }

        private bool HasSoleUnplayedCardStrictlyAbove(Card c, IEnumerable<Card> cardsPlayed)
        {
            if (IsCardHigh(c, cardsPlayed))
                return false;

            var r = RankSort(c);
            return CardsInSuit(c).Count(card => RankSort(card) > r && !IsCardRecordedAsPlayed(card, cardsPlayed)) == 1;
        }

        private static bool IsCardRecordedAsPlayed(Card card, IEnumerable<Card> cardsPlayed) =>
            cardsPlayed.Any(p => p.suit == card.suit && p.rank == card.rank);

        protected override Card TrySignalGoodSuit(PlayerBase player, IReadOnlyList<Card> legalCards, IReadOnlyList<Card> cardsPlayed, bool isDefending)
        {
            //  In no-trump, slough jokers before signaling a good suit (jokers are dead in NT)
            if (trump == Suit.Unknown && legalCards.Any(c => c.suit == Suit.Joker))
                return legalCards.First(c => c.suit == Suit.Joker);

            //  NT offense: dump from weakest suit rather than Lavinthal-style strength signals
            if (trump == Suit.Unknown && !isDefending && IsPartnership)
                return LowestCardFromWeakestSuitNT(legalCards, cardsPlayed);

            return base.TrySignalGoodSuit(player, legalCards, cardsPlayed, isDefending);
        }

        public override BidBase SuggestBid(SuggestBidState<WhistOptions> state)
        {
            var hand = state.hand;

            var suits = new List<Suit> { Suit.Unknown }.Concat(SuitRank.stdSuits).ToList();
            var lowIsHigh = options._lowIsHigh; // save

            options._lowIsHigh = false; // RankSort looks at this
            var highTricksBySuit = suits.ToDictionary(s => s, s => CountTricks(hand, s));
            var maxTrumpHighTricks = highTricksBySuit.Max(kvp => kvp.Key != Suit.Unknown ? kvp.Value : 0);

            options._lowIsHigh = true; // RankSort looks at this
            var lowTricksBySuit = suits.ToDictionary(s => s, s => CountTricks(hand, s));
            var maxTrumpLowTricks = lowTricksBySuit.Max(kvp => kvp.Key != Suit.Unknown ? kvp.Value : 0);

            var maxNotrumpTricks = Math.Max(highTricksBySuit[Suit.Unknown], lowTricksBySuit[Suit.Unknown]);

            options._lowIsHigh = lowIsHigh; // restore

            return state.legalBids.Where(b => b.value != BidBase.NoBid).OrderBy(b =>
            {
                var wb = new WhistBid(b);

                if (!wb.IsDeclareBid)
                    return -1;

                //  start with 1 extra trick for the widow and 3 for partner
                var tricks = 4;

                if (options.bidderGetsKitty)
                    tricks += 1; //  estimate one more trick if we get the kitty

                if (options._highBidderSeat.HasValue)
                {
                    //  second round of bidding
                    //  get the correct estimated tricks based on suit and whether high or low wins
                    tricks += (wb.HighWins ? highTricksBySuit : lowTricksBySuit)[wb.Suit];
                }
                else
                {
                    //  first round of bidding
                    if (wb.Suit == Suit.Unknown)
                    {
                        //  no-trump, take the best of high/low
                        tricks += maxNotrumpTricks;
                    }
                    else
                    {
                        //  trump, take the best suit's tricks depending on whether high or low wins
                        tricks += wb.HighWins ? maxTrumpHighTricks : maxTrumpLowTricks;
                    }
                }

                return tricks - wb.Tricks;
            }).Last();
        }

        public override List<Card> SuggestDiscard(SuggestDiscardState<WhistOptions> state)
        {
            var (player, hand) = (state.player, state.hand);

            List<Card> cards;

            var count = options.KittySize;
            var theBid = new WhistBid(player.Bid);

            if (theBid.Suit == Suit.Unknown)
            {
                //  in no-trump, throw the lowest cards we have, but make sure to get rid of Jokers first as they're useless here
                //  TODO: try to balance this by keeping cards we need to stop a running suit
                cards = hand.OrderBy(c => c.suit != Suit.Joker).ThenBy(RankSort).Take(count).ToList();
            }
            else
            {
                //  in trump, first group by suits to focus on creating void off-suits
                cards = hand.GroupBy(EffectiveSuit).ToDictionary(g => g.Key, g => g.OrderBy(RankSort).ToList())

                    //  save trump to discard last
                    .OrderBy(kvp => kvp.Key == trump)

                    //  try to get rid of off-suits with no cards we can make boss,
                    .ThenBy(kvp => 0 >= HighRankInSuit(kvp.Key) - RankSort(kvp.Value.Last()) - (kvp.Value.Count - 1))

                    //  followed by those that will take the longest to make a card boss
                    .ThenByDescending(kvp => HighRankInSuit(kvp.Key) - RankSort(kvp.Value.Last()))

                    //  then just get rid of the lowest cards in the shortest suits
                    .ThenBy(kvp => kvp.Value.Count)

                    //  now merge the suits into one flat list and take however many cards we need to discard
                    .SelectMany(kvp => kvp.Value).Take(count).ToList();
            }

            return cards;
        }

        public override Card SuggestNextCard(SuggestCardState<WhistOptions> state)
        {
            var bid = new WhistBid(state.player.Bid);
            var legalCards = state.legalCards;

            // Avoid leading Jokers or suits partner is known to be void in NT
            if (state.trick.Count == 0 && state.trumpSuit == Suit.Unknown) {
                if (legalCards.Any(c => c.suit == Suit.Joker) && legalCards.Any(c => c.suit != Suit.Joker))
                {
                    legalCards = legalCards.Where(c => c.suit != Suit.Joker).ToList();
                }
                var players = new PlayersCollectionBase(this, state.players);
                var avoidPartnerVoidSuits = SuitRank.stdSuits.Where(s =>
                    players.PartnerIsVoidInSuit(state.player, new Card(s, Rank.Ace), state.cardsPlayed)).ToList();
                if (avoidPartnerVoidSuits.Count > 0)
                {
                    var withoutPartnerVoidLead = legalCards.Where(c =>
                        !avoidPartnerVoidSuits.Contains(EffectiveSuit(c)) || IsCardHigh(c, state.cardsPlayed)).ToList();
                    if (withoutPartnerVoidLead.Count > 0)
                        legalCards = withoutPartnerVoidLead;
                }
            }

            return TryTakeEm(state.player,
                state.trick,
                legalCards,
                state.cardsPlayed,
                new PlayersCollectionBase(this, state.players),
                state.isPartnerTakingTrick,
                state.cardTakingTrick,
                !bid.IsDeclareBid && !bid.IsDeclarePartnerBid,
                state.cardsPlayedInOrder);
        }

        public override List<Card> SuggestPass(SuggestPassState<WhistOptions> state)
        {
            throw new NotImplementedException();
        }

        private int CountTricks(IEnumerable<Card> hand, Suit trumpSuit)
        {
            var deckBySuit = DeckBuilder.BuildDeck(DeckType).GroupBy(c => EffectiveSuit(c, trumpSuit)).ToDictionary(g => g.Key, g => g.OrderBy(c => RankSort(c, trumpSuit)).ToList());
            var handBySuit = SuitRank.stdSuits.ToDictionary(s => s, s => hand.Where(c => EffectiveSuit(c, trumpSuit) == s).OrderBy(c => RankSort(c, trumpSuit)).ToList());

            var tricks = 0;

            if (trumpSuit == Suit.Unknown)
            {
                //  in no-trump, count 1 trick for each joker
                tricks += hand.Count(c => c.suit == Suit.Joker);
            }
            else
            {
                //  trump suits are only "good" if we have at least 4 trump
                var trumpCards = handBySuit[trumpSuit];
                if (trumpCards.Count < 4)
                    return 0;

                //  with trump, count trump we can use on suits with singletons or voids
                foreach (var suit in SuitRank.stdSuits.Where(s => s != trumpSuit).ToList())
                {
                    var countInSuit = handBySuit[suit].Count;
                    if (countInSuit < 2)
                    {
                        var trumpIn = Math.Min(2 - countInSuit, trumpCards.Count);
                        trumpCards.RemoveRange(0, trumpIn);
                        tricks += trumpIn;
                    }
                }
            }

            //  then calculate the winners for each suit, accounting for gaps
            foreach (var suit in SuitRank.stdSuits)
            {
                var deck = deckBySuit[suit];
                var cards = handBySuit[suit];

                var highRank = RankSort(deck.Last(), trumpSuit);
                var nextHighestRank = highRank;
                var hasStopper = false;

                while (cards.Any())
                {
                    //  don't give credit for off-suit cards more than two steps below the highest rank in a trump contract
                    //  reasoning: too easy for other players to be void and trump in by that point
                    if (trumpSuit != Suit.Unknown && suit != trumpSuit && highRank - nextHighestRank > 2)
                        break;

                    var targetCard = cards.Last(); //  start with our next highest card
                    var targetRank = RankSort(targetCard, trumpSuit);
                    var gaps = deck.Count(c => targetRank < RankSort(c, trumpSuit) && RankSort(c, trumpSuit) <= nextHighestRank && !cards.Contains(c));
                    var below = cards.Count(c => RankSort(c, trumpSuit) < targetRank);

                    if (gaps > below)
                        break;

                    tricks++;
                    hasStopper = true;
                    nextHighestRank = targetRank;
                    cards.Remove(targetCard);
                    cards.RemoveRange(0, gaps);
                }

                //  if we're looking at no-trump and we don't have a stopper in all suits, bail
                if (trumpSuit == Suit.Unknown && !hasStopper)
                    return 0;
            }

            return tricks;
        }
    }
}