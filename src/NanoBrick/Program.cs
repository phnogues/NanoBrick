// nanoBrick — programme nanoFramework pour ESP32 + nRF24L01+
// ==============================================================
//
// L'ESP32 imite jusqu'à 4 télécommandes grises JM039TX à la fois (une par position du sélecteur des
// récepteurs) : 4 récepteurs × 2 sorties = 8 moteurs type Power Functions d'AliExpress. C'est le portage en C# de
// ../../tools/JM039TX/JM039TX.ino.
//
// Il se présente en Bluetooth sous le nom « NanoBrick » (service série « Nordic UART »)
// et reçoit des ordres texte, une ligne par ordre (les mêmes que la version Arduino). Le chiffre en tête
// (1 à 4) choisit le récepteur ; sans chiffre, c'est la position courante (ordre c) :
//
//   [p]a+ [n]  [p]a- [n]   n crans de la molette gauche (sortie A), sens 1 / sens 2   (défaut 1)
//   [p]b+ [n]  [p]b- [n]   n crans de la molette droite (sortie B)
//   [p]sa [n]  [p]sb [n]   bouton orange gauche / droit (stop) maintenu n × 100 ms     (défaut 3)
//   [p]w <n>               trames neutres pendant n × 100 ms
//   x                      stop de toutes les sorties des récepteurs servis
//   k 1|0                  maintien : les récepteurs servis restent « accrochés » 15 s, prolongé par chaque ordre
//   m <1234>               récepteurs servis par le maintien et par x (défaut 1234)
//   c <1-4>                position courante
//   p <0-3>                puissance -18 / -12 / -6 / 0 dBm (défaut 0 : plus fort, le récepteur sature)
//   f                      état : tour d'émission le plus long, envois réussis / ratés
//
// Boîtier batterie 4 sorties (télécommande blanche) : tout ou rien, pleine vitesse, pas de récepteur à choisir.
//   ha+ [n]  ha- [n] ... hd+ [n]  hd- [n]   sortie A à D dans un sens pendant n × 100 ms   (défaut 3)
//                                           (a : haut/bas, b : droite/gauche, c : haut/bas, d : droite/gauche)
//   hs1 [n]  hs2 [n]       bouton carré gauche / droit
//   hx [a-d]               arrête une sortie du boîtier, ou toutes ; x arrête aussi le boîtier
//   Le boîtier s'appaire à la première télécommande qu'il entend.
//
// Réponses : « OK <ordre> », « PLEIN » si trop de crans d'un coup, « ? » si l'ordre est inconnu.
// On peut tester avec l'appli PC ../Remote (mode Bluetooth), « nRF Connect » ou « Serial Bluetooth Terminal ».
//
// Câblage (adaptateur nRF24 alimenté en 5 V) : CE -> 4, CSN -> 5, SCK -> 18, MISO -> 19, MOSI -> 23.

using System;
using System.Device.Gpio;
using System.Diagnostics;
using System.Threading;
using nanoFramework.Device.Bluetooth.Spp;
using nanoFramework.Hardware.Esp32;

namespace NanoBrick
{
    public class Program
    {
        // ---- Branchements ----
        private const int PinCe = 4, PinCsn = 5, PinSck = 18, PinMiso = 19, PinMosi = 23;
        private const int BusSpi = 1;

        // ---- Réglages ----
        private const string NomBluetooth = "NanoBrick";
        private const int PuissanceParDefaut = 0;  // -18 dBm

        private static readonly object Verrou = new object();
        private static NordicSpp _bluetooth;
        private static Nrf24 _radio;
        private static Emetteur _emetteur;
        private static string _tampon = string.Empty;
        private static int _positionCourante = 1;

        public static void Main()
        {
            Configuration.SetPinFunction(PinMosi, DeviceFunction.SPI1_MOSI);
            Configuration.SetPinFunction(PinMiso, DeviceFunction.SPI1_MISO);
            Configuration.SetPinFunction(PinSck, DeviceFunction.SPI1_CLOCK);

            GpioController gpio = new GpioController();
            GpioPin ce = gpio.OpenPin(PinCe, PinMode.Output);
            _radio = new Nrf24(BusSpi, PinCsn, ce);
            if (!_radio.Demarrer(Jm039.Adresse, PuissanceParDefaut))
            {
                Debug.WriteLine("nRF24L01 non détecté : vérifie le câblage et l'alimentation");
                Thread.Sleep(Timeout.Infinite);
            }

            _emetteur = new Emetteur(_radio);
            _emetteur.Demarrer();

            _bluetooth = new NordicSpp();
            _bluetooth.ReceivedData += SurReception;
            _bluetooth.ConnectedEvent += SurConnexion;
            _bluetooth.Start(NomBluetooth);
            Debug.WriteLine("NanoBrick prêt, en attente d'une connexion Bluetooth...");

            Thread.Sleep(Timeout.Infinite);
        }

        private static void SurConnexion(IBluetoothSpp sender, EventArgs e)
        {
            Debug.WriteLine(sender.IsConnected ? "Connecté" : "Déconnecté");
            if (sender.IsConnected) return;

            _emetteur.Maintenir(false);  // la vraie télécommande peut reprendre
            // Après une déconnexion, la diffusion Bluetooth ne repart pas toujours d'elle-même :
            // on la relance (dans un autre fil, pas depuis l'événement de la pile Bluetooth).
            new Thread(() =>
            {
                Thread.Sleep(500);
                try
                {
                    _bluetooth.Stop();
                    Thread.Sleep(200);
                    _bluetooth.Start(NomBluetooth);
                    Debug.WriteLine("Diffusion Bluetooth relancée");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Relance Bluetooth impossible : " + ex.Message);
                }
            }).Start();
        }

        // Les données Bluetooth peuvent arriver en morceaux : on les découpe en lignes.
        private static void SurReception(IBluetoothSpp sender, SppReceivedDataEventArgs e)
        {
            string[] lignes;
            lock (Verrou)
            {
                _tampon += e.DataString;
                int fin = _tampon.LastIndexOf('\n');
                if (fin < 0)
                {
                    if (_tampon.Length > 64) _tampon = string.Empty;  // n'importe quoi : on jette
                    return;
                }
                lignes = _tampon.Substring(0, fin).Split('\n');
                _tampon = _tampon.Substring(fin + 1);
            }

            foreach (string ligne in lignes)
            {
                string ordre = ligne.Trim();
                if (ordre.Length > 0) Envoyer(Executer(ordre));
            }
        }

        /// <summary>Exécute un ordre texte et renvoie la réponse à transmettre.</summary>
        private static string Executer(string ordre)
        {
            // Chiffre en tête (1 à 4) = récepteur visé ; sans chiffre, la position courante (ordre c)
            int cible = _positionCourante;
            if (ordre.Length > 1 && ordre[0] >= '1' && ordre[0] <= '4' && ordre[1] >= 'a' && ordre[1] <= 'z')
            {
                cible = ordre[0] - '0';
                ordre = ordre.Substring(1);
            }

            if (ordre[0] == 'h') return ExecuterBoitier(ordre);

            string[] mots = ordre.Split(' ');
            string nom = mots[0];
            bool bouton = nom == "sa" || nom == "sb";
            int n = mots.Length > 1 ? LireNombre(mots[1]) : (bouton ? 3 : 1);
            if (n < 0) n = 0;

            bool ok = true;
            switch (nom)
            {
                case "a+": ok = _emetteur.Crans(cible, 0, Jm039.GaucheSens1, n); break;
                case "a-": ok = _emetteur.Crans(cible, 0, Jm039.GaucheSens2, n); break;
                case "b+": ok = _emetteur.Crans(cible, 1, Jm039.DroiteSens1, n); break;
                case "b-": ok = _emetteur.Crans(cible, 1, Jm039.DroiteSens2, n); break;
                case "sa": ok = _emetteur.Stop(cible, 0, n); break;
                case "sb": ok = _emetteur.Stop(cible, 1, n); break;
                case "w": ok = _emetteur.Attendre(cible, n); break;
                case "x": _emetteur.ToutArreter(); break;
                case "k": _emetteur.Maintenir(n != 0); return "OK k " + n;
                case "m": _emetteur.PositionsServies = LireMasque(mots.Length > 1 ? mots[1] : ""); break;
                case "c": _positionCourante = n >= 1 && n <= 4 ? n : 1; break;
                case "p": _radio.ChoisirPuissance(n); break;
                case "f":
                    return "ETAT position " + _positionCourante + ", tour max " + _emetteur.LireEtRemettreTourMax()
                           + " ms, envois ok " + _radio.EnvoisReussis + ", rates " + _radio.EnvoisRates;
                default:
                    return "? ordres : [1-4]a+ a- b+ b- [n] | [1-4]sa sb [n] | [1-4]w <n> | x | k 1|0 | m <1234> | c 1-4 | p 0-3 | f | h... (boitier)";
            }
            _emetteur.Prolonger();
            return (ok ? "OK " : "PLEIN ") + cible + ordre;
        }

        // Ordres du boîtier 4 sorties : « ha+ 5 », « hs1 », « hx », « hx b »
        private static string ExecuterBoitier(string ordre)
        {
            string[] mots = ordre.Split(' ');
            string nom = mots[0];
            int n = mots.Length > 1 ? LireNombre(mots[1]) : 3;
            if (n < 1) n = 1;

            if (nom.Length == 3 && nom[1] >= 'a' && nom[1] <= 'd' && (nom[2] == '+' || nom[2] == '-'))
                _emetteur.BoitierTourner(nom[1] - 'a', nom[2] == '+', n);
            else if (nom == "hs1" || nom == "hs2")
                _emetteur.BoitierCarre(nom[2] - '0', n);
            else if (nom == "hx")
            {
                string s = mots.Length > 1 ? mots[1].Trim() : "";
                _emetteur.BoitierArreter(s.Length == 1 && s[0] >= 'a' && s[0] <= 'd' ? s[0] - 'a' : -1);
            }
            else
                return "? boitier : ha+ ha- hb+ hb- hc+ hc- hd+ hd- [n] | hs1 hs2 [n] | hx [a-d]";
            return "OK " + ordre;
        }

        // « 13 » -> positions 1 et 3 (bit 0 = position 1)
        private static int LireMasque(string texte)
        {
            int masque = 0;
            foreach (char c in texte)
            {
                if (c >= '1' && c <= '4') masque |= 1 << (c - '1');
            }
            return masque;
        }

        private static int LireNombre(string texte)
        {
            try
            {
                return int.Parse(texte.Trim());
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static void Envoyer(string message)
        {
            Debug.WriteLine("-> " + message);
            if (_bluetooth != null && _bluetooth.IsConnected)
            {
                _bluetooth.SendString(message + "\n");
            }
        }
    }
}
