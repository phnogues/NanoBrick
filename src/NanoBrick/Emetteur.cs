using System;
using System.Threading;

namespace NanoBrick
{
    /// <summary>
    /// Imite jusqu'à 4 télécommandes JM039TX à la fois, une par position du sélecteur des récepteurs :
    /// 4 récepteurs × 2 sorties = 8 moteurs. Toutes les 8 ms, chaque position active reçoit sa trame,
    /// envoyée sur ses 4 canaux. Chaque moteur a sa propre file de commandes : A et B d'un même
    /// récepteur bougent ensemble. Un fil d'exécution dédié fait les envois.
    /// Il imite aussi la télécommande blanche du boîtier batterie 4 sorties (<see cref="Boitier"/>),
    /// émise dans le même tour, sur son canal et à son adresse.
    /// </summary>
    internal class Emetteur
    {
        public const int NombrePositions = 4;
        private const int AccrocheMs = 2000;   // trames neutres après un silence, le temps que le récepteur accroche
        private const int MaintienMs = 15000;  // durée du maintien, prolongée par chaque commande

        private readonly Nrf24 _radio;
        private readonly object _verrou = new object();
        private readonly byte[] _trame = new byte[Jm039.TailleTrame];
        private readonly Position[] _positions = new Position[NombrePositions];
        private long _maintienJusqua;   // en millisecondes

        // Boîtier 4 sorties : bouton appuyé et trames restantes par sortie (A à D), boutons carrés
        private readonly int[] _boitierBouton = new int[4];
        private readonly int[] _boitierRestant = new int[4];
        private int _boitierCarres, _boitierCarresRestant, _boitierFin;
        private readonly byte[][] _paquetsBoitier = new byte[256][];   // par combinaison de directions

        /// <summary>Positions servies par le maintien et par « tout arrêter » (bit 0 = position 1).</summary>
        public int PositionsServies { get; set; } = 0x0F;

        /// <summary>Durée du tour d'émission le plus long depuis la dernière lecture (diagnostic).</summary>
        public int TourMaxMs { get; private set; }

        public Emetteur(Nrf24 radio)
        {
            _radio = radio;
            for (int p = 0; p < NombrePositions; p++) _positions[p] = new Position();
        }

        public void Demarrer()
        {
            new Thread(Boucle).Start();
        }

        /// <summary>n crans de molette. sortie : 0 = A, 1 = B. Renvoie false si la file est pleine.</summary>
        public bool Crans(int position, int sortie, int commande, int n)
        {
            lock (_verrou)
            {
                Moteur m = _positions[position - 1].Moteurs[sortie];
                for (int i = 0; i < n; i++)
                {
                    if (!m.Ajouter(commande, Jm039.TramesParCran, Jm039.TramesEntreCrans)) return false;
                }
                return true;
            }
        }

        /// <summary>Bouton orange (stop) maintenu n dixièmes de seconde ; annule les crans en attente.</summary>
        public bool Stop(int position, int sortie, int dixiemes)
        {
            lock (_verrou)
            {
                Moteur m = _positions[position - 1].Moteurs[sortie];
                m.Vider();
                return m.Ajouter(sortie == 0 ? Jm039.StopGauche : Jm039.StopDroite, dixiemes * 100 / Jm039.PeriodeMs, 0);
            }
        }

        /// <summary>Stop sur toutes les sorties des positions servies.</summary>
        public void ToutArreter()
        {
            for (int p = 1; p <= NombrePositions; p++)
            {
                if ((PositionsServies & (1 << (p - 1))) == 0) continue;
                Stop(p, 0, 3);
                Stop(p, 1, 3);
            }
            BoitierArreter(-1);
        }

        /// <summary>
        /// Boîtier 4 sorties : sortie (0 = A … 3 = D) dans un sens pendant n dixièmes de seconde, à pleine
        /// vitesse (le boîtier ne connaît pas la vitesse). Les sorties peuvent tourner en même temps.
        /// </summary>
        public void BoitierTourner(int sortie, bool plus, int dixiemes)
        {
            lock (_verrou)
            {
                _boitierBouton[sortie] = Boitier.Bouton(sortie, plus);
                _boitierRestant[sortie] = dixiemes * 100 / Jm039.PeriodeMs;
            }
        }

        /// <summary>Boîtier : bouton carré (gauche = 1, droit = 2) appuyé n dixièmes de seconde.</summary>
        public void BoitierCarre(int carre, int dixiemes)
        {
            lock (_verrou)
            {
                _boitierCarres = carre == 1 ? Boitier.CarreGauche : Boitier.CarreDroit;
                _boitierCarresRestant = dixiemes * 100 / Jm039.PeriodeMs;
            }
        }

        /// <summary>Boîtier : arrête une sortie (0 à 3), ou toutes (-1).</summary>
        public void BoitierArreter(int sortie)
        {
            lock (_verrou)
            {
                for (int s = 0; s < 4; s++)
                {
                    if (sortie < 0 || s == sortie) _boitierRestant[s] = 0;
                }
                if (sortie < 0) _boitierCarresRestant = 0;
                _boitierFin = Boitier.TramesDeFin;
            }
        }

        /// <summary>Trames neutres pendant n dixièmes de seconde sur une position.</summary>
        public bool Attendre(int position, int dixiemes)
        {
            lock (_verrou) return _positions[position - 1].Moteurs[0].Ajouter(0, 0, dixiemes * 100 / Jm039.PeriodeMs);
        }

        /// <summary>Maintien : trames neutres en continu pour que les récepteurs servis restent accrochés.</summary>
        public void Maintenir(bool actif)
        {
            lock (_verrou) _maintienJusqua = actif ? Maintenant() + MaintienMs : 0;
        }

        /// <summary>Prolonge le maintien s'il est actif (appelé à chaque commande).</summary>
        public void Prolonger()
        {
            lock (_verrou)
            {
                if (_maintienJusqua > Maintenant()) _maintienJusqua = Maintenant() + MaintienMs;
            }
        }

        public int LireEtRemettreTourMax()
        {
            int t = TourMaxMs;
            TourMaxMs = 0;
            return t;
        }

        private void Boucle()
        {
            long prochaine = Maintenant();
            int[] commandes = new int[NombrePositions];
            bool[] aEmettre = new bool[NombrePositions];
            byte[] paquetBoitier;
            while (true)
            {
                long attente = prochaine - Maintenant();
                // Toujours une petite pause, même en retard : sinon les fils du Bluetooth n'ont plus la main
                Thread.Sleep(attente > 2 ? (int)attente : 2);
                prochaine += Jm039.PeriodeMs;
                if (Maintenant() - prochaine > 50) prochaine = Maintenant();  // gros retard (ramasse-miettes) : on repart

                long debut = Maintenant();
                lock (_verrou)
                {
                    bool maintien = _maintienJusqua > Maintenant();
                    for (int p = 0; p < NombrePositions; p++)
                    {
                        bool servie = maintien && (PositionsServies & (1 << p)) != 0;
                        aEmettre[p] = _positions[p].ProchaineTrame(servie, out commandes[p]);
                    }
                    paquetBoitier = ProchainPaquetBoitier();
                }
                // Envois hors du verrou : les ordres Bluetooth ne sont pas bloqués pendant ce temps
                for (int p = 0; p < NombrePositions; p++)
                {
                    if (aEmettre[p]) EnvoyerTrame(p, commandes[p]);
                }
                if (paquetBoitier != null)
                {
                    _radio.ChoisirAdresse(Boitier.RegistreAdresse);
                    _radio.EnvoyerPaquet(Boitier.Canal, paquetBoitier);
                }
                int duree = (int)(Maintenant() - debut);
                if (duree > TourMaxMs) TourMaxMs = duree;
            }
        }

        private void EnvoyerTrame(int p, int commande)
        {
            Position pos = _positions[p];
            byte[] paquet = Paquet(p, commande, pos.Bascule);
            pos.Bascule = !pos.Bascule;
            byte[] canaux = Jm039.CanauxDe(p + 1);
            _radio.ChoisirAdresse(Jm039.RegistreAdresse);
            for (int i = 0; i < canaux.Length; i++)
            {
                _radio.EnvoyerPaquet(canaux[i], paquet);
            }
        }

        // Calculer une trame (CRC compris) coûte cher en code interprété : chaque combinaison
        // (position, commande, bit de bascule) n'est calculée qu'une fois, puis gardée (512 au plus).
        private readonly byte[][] _paquets = new byte[NombrePositions * 64 * 2][];

        private byte[] Paquet(int p, int commande, bool bascule)
        {
            int indice = ((p * 64) + (commande & 63)) * 2 + (bascule ? 1 : 0);
            byte[] paquet = _paquets[indice];
            if (paquet == null)
            {
                Jm039.Construire(commande, p + 1, bascule, _trame);
                paquet = new byte[_trame.Length + 1];
                paquet[0] = Nrf24.CommandeEmission;
                Array.Copy(_trame, 0, paquet, 1, _trame.Length);
                _paquets[indice] = paquet;
            }
            return paquet;
        }

        // Trame du boîtier pour ce tour (sous le verrou), ou null s'il faut se taire. Comme la vraie
        // télécommande : une trame tant qu'un bouton est appuyé, puis 20 trames « rien », puis silence.
        private byte[] ProchainPaquetBoitier()
        {
            int directions = 0, carres = 0;
            bool actif = false;
            for (int s = 0; s < 4; s++)
            {
                if (_boitierRestant[s] == 0) continue;
                _boitierRestant[s]--;
                directions |= _boitierBouton[s];
                actif = true;
            }
            if (_boitierCarresRestant > 0)
            {
                _boitierCarresRestant--;
                carres = _boitierCarres;
                actif = true;
            }
            if (actif) _boitierFin = Boitier.TramesDeFin;
            else if (_boitierFin > 0) _boitierFin--;
            else return null;

            if (carres != 0) return PaquetBoitier(directions, carres);  // rare : pas de cache
            byte[] paquet = _paquetsBoitier[directions];
            if (paquet == null) _paquetsBoitier[directions] = paquet = PaquetBoitier(directions, 0);
            return paquet;
        }

        private static byte[] PaquetBoitier(int directions, int carres)
        {
            var trame = new byte[Boitier.TailleTrame];
            Boitier.Construire(directions, carres, trame);
            var paquet = new byte[trame.Length + 1];
            paquet[0] = Nrf24.CommandeEmission;
            Array.Copy(trame, 0, paquet, 1, trame.Length);
            return paquet;
        }

        private static long Maintenant()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        /// <summary>Un récepteur : ses deux moteurs et l'état de sa liaison.</summary>
        private class Position
        {
            public readonly Moteur[] Moteurs = { new Moteur(), new Moteur() };
            public bool Bascule;
            private bool _silencieux = true;
            private int _accroche, _fin;

            // Décide de la trame à émettre. Renvoie false s'il faut se taire.
            public bool ProchaineTrame(bool maintien, out int commande)
            {
                commande = 0;
                bool enAttente = Moteurs[0].Occupe || Moteurs[1].Occupe;
                if (enAttente && _silencieux)
                {
                    _accroche = AccrocheMs / Jm039.PeriodeMs;
                    _silencieux = false;
                }
                if (_accroche > 0) { _accroche--; return true; }
                if (enAttente)
                {
                    commande = Moteurs[0].Avancer() | Moteurs[1].Avancer();
                    if (commande != 0) _fin = Jm039.TramesDeFin;
                    return true;
                }
                // Maintien : une trame neutre à chaque tour (un tour sur trois ne suffit pas : la lumière du
                // récepteur clignote). Avec 4 positions maintenues, l'ESP32 en C# est débordé : servir
                // seulement les récepteurs présents (ordre m).
                if (maintien) { _silencieux = false; return true; }
                if (_fin > 0) { _fin--; return true; }
                _silencieux = true;  // plus rien à dire : on se tait, comme la vraie télécommande
                return false;
            }
        }

        /// <summary>Une sortie d'un récepteur, avec sa file de travaux (commande, trames actives, trames neutres).</summary>
        private class Moteur
        {
            private const int Taille = 32;
            private readonly int[] _commande = new int[Taille];
            private readonly int[] _actives = new int[Taille];
            private readonly int[] _neutres = new int[Taille];
            private int _tete, _queue;
            private int _cmd, _act, _neu;   // travail en cours

            public bool Occupe
            {
                get { return _act > 0 || _neu > 0 || _tete != _queue; }
            }

            public bool Ajouter(int commande, int actives, int neutres)
            {
                int suivant = (_queue + 1) % Taille;
                if (suivant == _tete) return false;
                _commande[_queue] = commande;
                _actives[_queue] = actives;
                _neutres[_queue] = neutres;
                _queue = suivant;
                return true;
            }

            public void Vider()
            {
                _tete = _queue = 0;
                _act = _neu = 0;
            }

            // Commande de ce moteur pour la trame en cours (0 = neutre)
            public int Avancer()
            {
                if (_act == 0 && _neu == 0 && _tete != _queue)
                {
                    _cmd = _commande[_tete];
                    _act = _actives[_tete];
                    _neu = _neutres[_tete];
                    _tete = (_tete + 1) % Taille;
                }
                if (_act > 0) { _act--; return _cmd; }
                if (_neu > 0) _neu--;
                return 0;
            }
        }
    }
}
