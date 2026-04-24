# MESORBStrategy — Contexte Strategie NinjaTrader — MAJ 2026-04-24

## Objectif
Premiere strategie "Hello World" propre apres l'abandon du portage Momentum FVG sur crypto CME.
**Opening Range Breakout (ORB) 30 minutes sur MES (Micro S&P 500 futures)** — session RTH US.

Choisi comme baseline debutant car :
- MES = future le plus liquide au monde (spread quasi nul)
- ORB = mecanique pure, pas d'interpretation subjective
- Edge documente depuis 20+ ans en academic papers
- Simple a coder en NinjaScript (< 100 lignes de logique)
- Session RTH US (9h30-16h NY) = 21h30-04h Cambodge (compatible fuseau)
- Commissions absorbables (~1,40$/RT sur ~3-5 trades/jour)

## Utilisateur
- Trader FR resident au **Cambodge** (Siem Reap)
- C#/NinjaScript
- Setup dev : Mac + Parallels Windows (NinjaTrader cote Windows)
- Capital trading : **10 000$**

## Rappels theoriques (session 2026-04-24)

### RTH vs ETH
- **RTH = Regular Trading Hours** = session cash US = **9h30 -> 16h00 NY**
- **ETH = Extended Trading Hours** = hors RTH = pre-market + after-hours + sessions asiatique/europeenne
- Les futures MES sont dispo **23h/24, 5j/7** (CME Globex), mais l'ORB exige **RTH uniquement**
- Hors RTH = volume faible, spreads larges, faux breakouts

**Attention au sigle "ETH"** : dans le contexte futures = Extended Trading Hours ; dans le contexte crypto = Ethereum.

### NYSE vs CME
- **NYSE / Nasdaq** = bourses cash d'actions individuelles (Apple, Tesla...), ouvertes **9h30-16h NY uniquement**
- **CME** (Chicago Mercantile Exchange) = bourse de futures/derives, ouverte **23h/24**
- MES = future **CME** qui suit l'indice S&P 500 (panier de 500 actions NYSE/Nasdaq)
- CME = zero actions individuelles, uniquement indices, metaux, energie, devises, taux, agricoles, crypto futures
- Le trader touche uniquement le CME via NinjaTrader (pas de connexion directe NYSE)

### Configuration NT8 obligatoire pour ORB
- Chart MES : **Trading Hours = "CME US Index Futures RTH"** (pas ETH)
- Resultat : bougies 9h30-16h NY uniquement, opening range clair a 9h30

## Recette minimum viable (V1 baseline)

### Regles
1. **Session** : RTH US (9h30-16h NY)
2. **Range** : high/low des **30 premieres minutes** (9h30-10h00 NY)
3. **Entry Long** : close 5min > high de la range + volume filter
4. **Entry Short** : close 5min < low de la range + volume filter
5. **Stop** : opposite side of range
6. **Target** : 2x la hauteur de la range OU trailing ATR 3x
7. **Time stop** : flat a **15h45 NY** (avant close RTH)
8. **1 trade par jour max** (pas de re-entry)

### Parametres par defaut
- **Timeframe** : 5min
- **Range period** : 30 min (6 bougies 5min)
- **Volume filter** : volume > MA(volume, 20) x 1,3
- **Sizing** : 1 MES = ~10-15$ par tick move
- **Commissions** : ~1,40$/RT absorbable sur 3-5 trades/jour
- **Target** : 2x range (fixe) OU trailing ATR 3x (a arbitrer apres backtest)

### Indicateurs utilises (minimalistes — 4 max)
1. **Opening Range** (custom, 30 min) — high/low des 6 premieres bougies
2. **Volume + VolumeMA(20)** — filtre anti-faux breakout, seuil x1,3
3. **ATR(14)** — trailing stop 3x ATR (optionnel V1)
4. **VWAP** — filtre directionnel (optionnel V1, puissant en V2)

**NE PAS ajouter** : RSI, MACD, Stochastique, Bollinger, EMA multiples, Ichimoku, Fibonacci.
Philosophie ORB = niveau de prix objectif, pas d'analyse technique subjective.

### A tester (post-backtest V1)
- Skip Mardi/Mercredi selon backtest (observation empirique ORB classique)
- Target fixe 2x range VS trailing ATR 3x
- Ajout filtre VWAP (long seulement si close > VWAP)
- Range 15min VS 30min VS 60min

## Auto-export stats
- Fichier : `C:\Mac\Home\Documents\NinjaTrader 8\last_backtest_MESORBStrategy.log`
- Ecrit via `NinjaTrader.Core.Globals.UserDataDir` (compatibilite Parallels Mac/Windows)
- Update automatique a chaque backtest (State.Terminated)
- Meme pattern que BTCMomentumFVG / ETHMomentumFVG

## Workflow technique
- Code edite sur Mac : `/Users/lead_scorpion/Desktop/Claude/MESORBStrategy/`
- Copie vers `/Users/lead_scorpion/Documents/NinjaTrader 8/bin/Custom/Strategies/MESORBStrategy.cs`
- NinjaTrader recompile auto (F5 pour forcer)
- Git repo : https://github.com/leadscorpion777/MESORBStrategy

## Parametres NinjaTrader importants
- Calcul a la fermeture de la barre (OnBarClose)
- BarsMax = Infini
- Comportement depart : "Attendre d'etre a plat"
- Sortie sur fermeture de session : **COCHE** (intraday, 1 trade/jour, flatter a 15h45 NY)
- Arret en fin de journee : DECOCHE (le code gere lui-meme le time stop 15h45)
- Jours a charger : minimum 60 (pour VolumeMA 20 + ATR 14 + stabilite stats)
- Session template : **CME US Index Futures RTH** (IMPORTANT)

## Criteres de validation V1
Avant de passer a V2 / portage MNQ-MGC, V1 doit respecter :
- PF > 1,3 sur 2+ ans de backtest RTH MES 5min
- Max DD < 5% capital 10k (soit < 500$)
- Winrate > 35% (acceptable pour trend/breakout 2:1 R:R)
- Expectancy positive apres commissions (1,40$/RT x 500 trades = 700$ frais sur 2 ans)
- Pas plus de 3 mois perdants consecutifs

## Objectif portfolio (rappel strategie globale)
Apres ORB MES V1 validee :
- **Portage ORB sur MNQ** (momentum tech, plus volatile)
- **Portage ORB sur MGC** (gold, decorrelation macro)
- **Ajout Mean-Reversion 5-15min** (Z-Score + ADX < 20) sur MES
- **Ajout Donchian 20 swing** sur MGC
- Objectif : portfolio 3-4 strats decorrelees pour lisser variance mensuelle
- Cible revenu : 500-1000$/mois avec 10k capital

## A faire / roadmap

### Immediat
- [ ] Coder MESORBStrategy.cs V1 (logique ORB complete)
- [ ] Tester compilation NT8
- [ ] Backtest RTH MES 5min, 2+ ans
- [ ] Ajuster parametres selon resultats

### Apres V1 validee
- [ ] V2 : ajouter filtre VWAP directionnel
- [ ] V2 : tester trailing ATR 3x vs target fixe
- [ ] V2 : skip Mardi/Mercredi selon stats
- [ ] Portage MNQ, MGC
- [ ] Ajout Mean-Reversion decorrelee

## Lecons retenues (session 23-24 avril 2026)
1. Toujours verifier le **type d'instrument** (Future vs CFD) avant tout dev
2. Session template **coherente avec l'instrument** (RTH pour ORB MES, pas ETH)
3. "Arret en fin de journee" DECOCHE en general — gere le time stop dans le code
4. Backtest sur **instrument tradable directement**
5. PF > 1,3-1,5 requis en OOS avant de parler d'edge reel
6. Strat simple qui marche > strat complexe qui mirage
