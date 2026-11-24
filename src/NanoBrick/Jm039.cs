namespace NanoBrick
{
    /// <summary>
    /// Protocole de la télécommande grise JM039TX (molettes), décodé au renifleur.
    /// Partie « pure » : aucun accès au matériel, elle se teste aussi sur PC (projet NanoBrick.Tests).
    /// Détail du protocole : moteurs/CLAUDE.md.
    /// </summary>
    public static class Jm039
    {
        /// <summary>Adresse de la télécommande, dans l'ordre d'émission.</summary>
        public static readonly byte[] Adresse = { 0x88, 0xB4, 0x87, 0x2E, 0xCB };

        /// <summary>La même, dans l'ordre du registre TX_ADDR du nRF24 (à l'envers).</summary>
        public static readonly byte[] RegistreAdresse = { 0xCB, 0x2E, 0x87, 0xB4, 0x88 };

        public const int PeriodeMs = 8;          // une trame toutes les 8 ms
        public const int TramesParCran = 5;      // trames actives par cran de molette
        public const int TramesEntreCrans = 6;   // trames neutres entre deux crans
        public const int TramesDeFin = 19;       // trames neutres avant de se taire
        public const int TailleTrame = 13;       // 11 octets de données + 2 de CRC

        // Commandes, combinables (OU binaire)
        public const int GaucheSens1 = 1;
        public const int GaucheSens2 = 2;
        public const int DroiteSens1 = 4;
        public const int DroiteSens2 = 8;
        public const int StopGauche = 16;   // bouton orange gauche
        public const int StopDroite = 32;   // bouton orange droit

        // Canaux de chaque position du sélecteur (le récepteur n'écoute que la sienne)
        private static readonly byte[][] Canaux =
        {
            new byte[] { 12, 13, 57, 58 },
            new byte[] { 9, 10, 52, 53 },
            new byte[] { 7, 8, 41, 42 },
            new byte[] { 19, 20, 67, 68 },
        };

        // Octet de sélection S et octet de contrôle D2 : XOR selon la position
        private static readonly byte[] SelectionXor = { 0x00, 0x01, 0x02, 0x07 };
        private static readonly byte[] ControleXor = { 0x00, 0x03, 0x02, 0x01 };

        // Trame neutre, position 1 : 34 CA T 92 D6 7E 6A B S D1 D2
        private static readonly byte[] TrameDeBase = { 0x34, 0xCA, 0x42, 0x92, 0xD6, 0x7E, 0x6A, 0xED, 0x29, 0xF9, 0x1A };

        public static byte[] CanauxDe(int position)
        {
            return Canaux[Borner(position) - 1];
        }

        /// <summary>
        /// Construit les 13 octets qui suivent l'adresse : données puis CRC.
        /// Les bits de commande valent 0 quand ils sont actifs ; l'octet de contrôle D2 suit
        /// une relation linéaire relevée au renifleur (pas de formule plus simple trouvée).
        /// </summary>
        public static void Construire(int commandes, int position, bool bascule, byte[] trame)
        {
            int i = Borner(position) - 1;
            for (int k = 0; k < TrameDeBase.Length; k++) trame[k] = TrameDeBase[k];

            if (bascule) trame[2] |= 0x04;     // bit qui change à chaque trame
            trame[8] ^= SelectionXor[i];
            trame[10] ^= ControleXor[i];

            if ((commandes & GaucheSens1) != 0) { trame[9] ^= 0x80; trame[10] ^= 0x80; }
            if ((commandes & GaucheSens2) != 0) { trame[9] ^= 0x40; trame[10] ^= 0x40; }
            if ((commandes & DroiteSens1) != 0) { trame[9] ^= 0x20; trame[10] ^= 0x60; }
            if ((commandes & DroiteSens2) != 0) { trame[9] ^= 0x10; trame[10] ^= 0x10; }
            if ((commandes & StopGauche) != 0) { trame[7] ^= 0x04; trame[10] ^= 0x0C; }
            if ((commandes & StopDroite) != 0) { trame[7] ^= 0x08; trame[10] ^= 0x08; }

            // CRC16-CCITT sur l'adresse puis les données, valeur de départ 0x8ABC (pas celle du nRF24)
            ushort crc = Crc16(Adresse, 0, Adresse.Length, 0x8ABC);
            crc = Crc16(trame, 0, 11, crc);
            trame[11] = (byte)(crc >> 8);
            trame[12] = (byte)(crc & 0xFF);
        }

        public static ushort Crc16(byte[] donnees, int debut, int longueur, ushort crc)
        {
            for (int i = debut; i < debut + longueur; i++)
            {
                crc ^= (ushort)(donnees[i] << 8);
                for (int b = 0; b < 8; b++)
                {
                    crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
                }
            }
            return crc;
        }

        private static int Borner(int position)
        {
            return position < 1 || position > 4 ? 1 : position;
        }
    }
}
