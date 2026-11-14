using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using GorillaNetworking;
using Photon.Pun;
using UnityEngine;
using UnityEngine.XR;

namespace ModCheckerPlugin
{
    public class MenuButton : MonoBehaviour
    {
        public Action OnPress;
        public TextMesh Text;
        float cooldown;
        public void Press()
        {
            if (Time.time < cooldown) return;
            cooldown = Time.time + 0.4f;
            OnPress?.Invoke();
        }
    }

    // Everything the checker shows about one player (real or test rig)
    class Info
    {
        public string Nick, Platform, Joined, Muted;
        public Color Col;
        public string Fps;
        public Transform Head;
        public bool Dummy;
        public List<string> Legal = new List<string>();
        public List<string> Illegal = new List<string>();
    }

    [BepInPlugin("com.example.modchecker", "ModChecker", "3.0.0")]
    public class ModChecker : BaseUnityPlugin
    {
        // ---------- Config ----------
        ConfigEntry<string> cfgLang, cfgIllegal, cfgLegal;
        bool polish;

        // ---------- State ----------
        bool on, yPrev, xPrev, trigPrev, hopping;
        VRRig selRig;            // selected real player
        bool selDummy;           // test rig selected
        int reasonIndex;
        string status = "";
        float statusUntil;

        static readonly GorillaPlayerLineButton.ButtonType[] ReasonTypes =
        {
            GorillaPlayerLineButton.ButtonType.HateSpeech,
            GorillaPlayerLineButton.ButtonType.Cheating,
            GorillaPlayerLineButton.ButtonType.Toxicity
        };
        string[] ReasonNames => polish
            ? new[] { "Mowa nienawisci", "Cheaty / mody", "Toksycznosc" }
            : new[] { "Hate speech", "Cheating", "Toxicity" };

        readonly Dictionary<string, float> firstSeen = new Dictionary<string, float>();
        string lastRoom = "";
        float roomJoinTime;

        // Test rig
        GameObject dummy;
        Transform dummyHead;
        bool dummyMuted;
        float dummySpawnTime;

        // Objects
        LineRenderer laser;
        TextMesh label, statusText;
        GameObject menuRoot;
        MenuButton btnLang, btnHop, btnTest, btnMute, btnReason, btnReport;

        string T(string en, string pl) => polish ? pl : en;

        void Awake()
        {
            cfgLang = Config.Bind("General", "Language", "EN", "EN or PL (also switchable in-game)");
            cfgIllegal = Config.Bind("Mods", "IllegalKeys",
                "genesis,violet,obsidian,elux,vortex,colossal,untitled,stupid",
                "Comma separated, lowercase. Photon property keys counted as ILLEGAL mods.");
            cfgLegal = Config.Bind("Mods", "LegalKeys",
                "cosmetx,gorillainfo",
                "Comma separated, lowercase. Photon property keys counted as LEGAL mods.");
            polish = cfgLang.Value.ToUpper() == "PL";

            var lg = new GameObject("MC_Laser");
            laser = lg.AddComponent<LineRenderer>();
            laser.positionCount = 2;
            laser.startWidth = laser.endWidth = 0.008f;
            laser.material = new Material(Shader.Find("Sprites/Default"));
            laser.startColor = laser.endColor = Color.cyan;
            laser.enabled = false;

            var tg = new GameObject("MC_Label");
            label = tg.AddComponent<TextMesh>();
            label.richText = true;
            label.fontSize = 48;
            label.characterSize = 0.025f;
            label.anchor = TextAnchor.LowerCenter;
            label.alignment = TextAlignment.Center;
            tg.SetActive(false);

            BuildMenu();
        }


        // ---------- Helpers that survive game updates ----------
        VRRig[] rigCache;
        float rigCacheTime;
        VRRig[] AllRigs()
        {
            if (rigCache == null || Time.time - rigCacheTime > 1f)
            {
                rigCache = UnityEngine.Object.FindObjectsOfType<VRRig>();
                rigCacheTime = Time.time;
            }
            return rigCache;
        }

        const System.Reflection.BindingFlags BF =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance;

        static object GetMember(object o, params string[] names)
        {
            if (o == null) return null;
            var t = o.GetType();
            foreach (var n in names)
            {
                try
                {
                    var f = t.GetField(n, BF);
                    if (f != null) return f.GetValue(o);
                    var pr = t.GetProperty(n, BF);
                    if (pr != null) return pr.GetValue(o, null);
                }
                catch { }
            }
            return null;
        }

        static string GetFps(VRRig rig)
        {
            var v = GetMember(rig, "fps", "FPS", "frameRate", "currentFps");
            try { if (v != null) return Convert.ToInt32(v).ToString(); } catch { }
            return "?";
        }

        // ---------- Menu ----------
        void BuildMenu()
        {
            menuRoot = new GameObject("MC_Menu");
            statusText = MakeText(menuRoot.transform, new Vector3(0, 0.125f, -0.008f), 0.011f);

            btnLang   = MakeButton(0.085f, () => SetLang(!polish));
            btnHop    = MakeButton(0.045f, () => { if (!hopping) StartCoroutine(HopRoutine()); });
            btnTest   = MakeButton(0.005f, ToggleDummy);
            btnMute   = MakeButton(-0.035f, DoMute);
            btnReason = MakeButton(-0.075f, () => reasonIndex = (reasonIndex + 1) % ReasonTypes.Length);
            btnReport = MakeButton(-0.115f, DoReport);
            menuRoot.SetActive(false);
        }

        MenuButton MakeButton(float y, Action act)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.transform.SetParent(menuRoot.transform, false);
            go.transform.localPosition = new Vector3(0, y, 0);
            go.transform.localScale = new Vector3(0.18f, 0.034f, 0.01f);
            go.GetComponent<Renderer>().material.color = new Color(0.1f, 0.1f, 0.18f);
            var b = go.AddComponent<MenuButton>();
            b.OnPress = act;
            b.Text = MakeText(menuRoot.transform, new Vector3(0, y, -0.008f), 0.010f);
            return b;
        }

        TextMesh MakeText(Transform parent, Vector3 pos, float size)
        {
            var g = new GameObject("t");
            g.transform.SetParent(parent, false);
            g.transform.localPosition = pos;
            var tm = g.AddComponent<TextMesh>();
            tm.fontSize = 48;
            tm.characterSize = size;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = Color.white;
            return tm;
        }

        void SetLang(bool pl) { polish = pl; cfgLang.Value = pl ? "PL" : "EN"; }
        void Status(string s) { status = s; statusUntil = Time.time + 3f; }

        void RefreshMenuTexts()
        {
            btnLang.Text.text = T("Language: EN", "Jezyk: PL");
            btnHop.Text.text = hopping ? T("Hopping...", "Skacze...") : T("Lobby hop", "Zmien lobby");
            btnTest.Text.text = dummy != null ? T("Remove test rig", "Usun test riga") : T("Spawn test rig", "Postaw test riga");
            btnMute.Text.text = T("Mute / Unmute", "Wycisz / Odcisz");
            btnReason.Text.text = T("Reason: ", "Powod: ") + ReasonNames[reasonIndex];
            btnReport.Text.text = T("REPORT", "ZGLOS");

            var sel = SelectedInfo();
            statusText.text = Time.time < statusUntil ? status
                : (sel != null ? T("Target: ", "Cel: ") + sel.Nick : T("Aim + trigger = select", "Celuj + trigger = wybierz"));
        }

        // ---------- Input ----------
        static bool Pressed(XRNode node, InputFeatureUsage<bool> usage)
        {
            var d = InputDevices.GetDeviceAtXRNode(node);
            return d.isValid && d.TryGetFeatureValue(usage, out bool v) && v;
        }

        void Update()
        {
            if (GorillaTagger.Instance == null) return;

            TrackRoom();

            bool y = Pressed(XRNode.LeftHand, CommonUsages.secondaryButton);   // Y
            if (y && !yPrev) on = !on;
            yPrev = y;

            bool x = Pressed(XRNode.LeftHand, CommonUsages.primaryButton);     // X
            if (x && !xPrev) SetLang(!polish);
            xPrev = x;

            laser.enabled = on;
            menuRoot.SetActive(on);
            if (!on) { label.gameObject.SetActive(false); return; }

            Transform hand = GorillaTagger.Instance.rightHandTransform;
            Vector3 origin = hand.position, dir = hand.forward;
            float len = 30f;

            Transform lh = GorillaTagger.Instance.leftHandTransform;
            Vector3 cam = Camera.main.transform.position;
            menuRoot.transform.position = lh.position + Vector3.up * 0.2f;
            menuRoot.transform.rotation = Quaternion.LookRotation(menuRoot.transform.position - cam);
            RefreshMenuTexts();

            bool trig = Pressed(XRNode.RightHand, CommonUsages.triggerButton);
            bool trigDown = trig && !trigPrev;
            trigPrev = trig;

            // Laser on a menu button?
            MenuButton hitBtn = null;
            foreach (var h in Physics.RaycastAll(origin, dir, 5f, ~0, QueryTriggerInteraction.Collide).OrderBy(h => h.distance))
            {
                var b = h.collider.GetComponent<MenuButton>();
                if (b != null) { hitBtn = b; len = h.distance; break; }
            }

            var selInfo = SelectedInfo();

            if (hitBtn != null)
            {
                laser.SetPosition(0, origin);
                laser.SetPosition(1, origin + dir * len);
                if (trigDown) hitBtn.Press();
                ShowLabel(selInfo);
                return;
            }

            // Otherwise aim at players / test rig
            bool found = FindTarget(origin, dir, out VRRig aimRig, out bool aimDummy);
            Info aimedInfo = found ? InfoFor(aimRig, aimDummy) : null;

            laser.SetPosition(0, origin);
            laser.SetPosition(1, aimedInfo != null ? aimedInfo.Head.position : origin + dir * len);

            if (aimedInfo != null && trigDown)
            {
                selRig = aimRig; selDummy = aimDummy;
                Status(T("Selected: ", "Wybrano: ") + aimedInfo.Nick);
            }

            ShowLabel(aimedInfo ?? selInfo);
        }

        void ShowLabel(Info i)
        {
            if (i == null) { label.gameObject.SetActive(false); return; }
            string hex = ColorUtility.ToHtmlStringRGB(i.Col);
            string rgb = $"{Mathf.RoundToInt(i.Col.r * 9)} {Mathf.RoundToInt(i.Col.g * 9)} {Mathf.RoundToInt(i.Col.b * 9)}";

            string text =
                (i.Dummy ? T("[TEST RIG] ", "[TEST RIG] ") : "") + i.Nick + "\n" +
                $"{T("Color", "Kolor")}: <color=#{hex}>■■■</color> ({rgb})\n" +
                $"FPS: {i.Fps}\n" +
                $"{T("Platform", "Platforma")}: {i.Platform}\n" +
                $"{T("Joined", "Dolaczyl")}: {i.Joined}\n" +
                $"{T("Muted", "Wyciszony")}: {i.Muted}\n" +
                $"{T("Mods", "Mody")}: <color=#55ff55>{T("legal", "legalne")} {i.Legal.Count}</color> | " +
                $"<color=#ff5555>{T("illegal", "nielegalne")} {i.Illegal.Count}</color>";
            if (i.Illegal.Count > 0) text += "\n<color=#ff5555>" + string.Join(", ", i.Illegal) + "</color>";
            if (i.Legal.Count > 0) text += "\n<color=#55ff55>" + string.Join(", ", i.Legal) + "</color>";

            label.text = text;
            Transform t = label.transform;
            t.position = i.Head.position + Vector3.up * 0.5f;
            t.rotation = Quaternion.LookRotation(t.position - Camera.main.transform.position);
            label.gameObject.SetActive(true);
        }

        // ---------- Targets ----------
        bool FindTarget(Vector3 origin, Vector3 dir, out VRRig bestRig, out bool bestDummy)
        {
            bestRig = null; bestDummy = false;
            float bestAngle = 8f;
            foreach (var rig in AllRigs())
            {
                if (rig == null || rig.isOfflineVRRig) continue;
                float a = Vector3.Angle(dir, rig.headMesh.transform.position - origin);
                if (a < bestAngle) { bestAngle = a; bestRig = rig; bestDummy = false; }
            }
            if (dummy != null)
            {
                float a = Vector3.Angle(dir, dummyHead.position - origin);
                if (a < bestAngle) { bestAngle = a; bestRig = null; bestDummy = true; }
            }
            return bestRig != null || bestDummy;
        }

        Info SelectedInfo()
        {
            if (selDummy) return dummy != null ? InfoFor(null, true) : null;
            if (selRig != null) return InfoFor(selRig, false);
            return null;
        }

        Info InfoFor(VRRig rig, bool isDummy) => isDummy ? DummyInfo() : RigInfo(rig);

        Info RigInfo(VRRig rig)
        {
            var np = rig.OwningNetPlayer;
            var i = new Info
            {
                Nick = np != null ? np.NickName : "?",
                Col = rig.playerColor,
                Fps = GetFps(rig),
                Head = rig.headMesh.transform,
                Platform = PlatformOf(rig),
                Joined = JoinedText(rig)
            };

            var props = np?.GetPlayerRef()?.CustomProperties;
            if (props != null)
            {
                var ill = Split(cfgIllegal.Value);
                var leg = Split(cfgLegal.Value);
                foreach (var key in props.Keys)
                {
                    string k = key.ToString().ToLower();
                    if (ill.Any(k.Contains)) i.Illegal.Add(key.ToString());
                    else if (leg.Any(k.Contains)) i.Legal.Add(key.ToString());
                }
            }

            var line = FindLine(np);
            i.Muted = line != null && line.muteButton != null && line.muteButton.isOn ? T("yes", "tak") : T("no", "nie");
            return i;
        }


        string PlatformOf(VRRig rig)
        {
            string cos = GetMember(rig, "concatStringOfCosmeticsAllowed", "rawCosmeticString", "cosmeticsString") as string;
            if (cos == null) return T("unknown", "nieznana");
            return cos.Contains("FIRST LOGIN") ? "Steam / PC" : T("Quest (standalone)", "Quest (samodzielny)");
        }

        Info DummyInfo()
        {
            var i = new Info
            {
                Dummy = true,
                Nick = "TestRig",
                Col = new Color(0.9f, 0.2f, 0.8f),
                Fps = "72",
                Head = dummyHead,
                Platform = T("Quest (standalone)", "Quest (samodzielny)"),
                Muted = dummyMuted ? T("yes", "tak") : T("no", "nie")
            };
            int s = (int)(Time.time - dummySpawnTime);
            i.Joined = T($"{s}s ago", $"{s}s temu");
            // Fake mods, classified with the same lists as real players
            foreach (var key in new[] { "genesis", "violet", "cosmetx" })
            {
                if (Split(cfgIllegal.Value).Any(key.Contains)) i.Illegal.Add(key);
                else if (Split(cfgLegal.Value).Any(key.Contains)) i.Legal.Add(key);
            }
            return i;
        }

        static string[] Split(string s) =>
            s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLower()).ToArray();

        // ---------- Test rig (works offline, also while banned) ----------
        void ToggleDummy()
        {
            if (dummy != null)
            {
                Destroy(dummy);
                dummy = null; dummyHead = null; selDummy = false;
                return;
            }

            Transform cam = Camera.main.transform;
            Vector3 fwd = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
            Vector3 pos = cam.position + fwd * 2f;

            dummy = new GameObject("MC_TestRig");
            dummy.transform.position = new Vector3(pos.x, cam.position.y - 0.9f, pos.z);

            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.transform.SetParent(dummy.transform, false);
            body.transform.localPosition = new Vector3(0, 0.5f, 0);
            body.transform.localScale = new Vector3(0.4f, 0.5f, 0.4f);
            body.GetComponent<Renderer>().material.color = new Color(0.9f, 0.2f, 0.8f);
            Destroy(body.GetComponent<Collider>());

            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.transform.SetParent(dummy.transform, false);
            head.transform.localPosition = new Vector3(0, 1.3f, 0);
            head.transform.localScale = Vector3.one * 0.3f;
            head.GetComponent<Renderer>().material.color = new Color(0.9f, 0.2f, 0.8f);
            Destroy(head.GetComponent<Collider>());

            dummyHead = head.transform;
            dummyMuted = false;
            dummySpawnTime = Time.time;
            Status(T("Test rig spawned", "Test rig postawiony"));
        }

        // ---------- Join-time tracking ----------
        void TrackRoom()
        {
            string room = PhotonNetwork.InRoom ? PhotonNetwork.CurrentRoom.Name : "";
            if (room != lastRoom)
            {
                lastRoom = room;
                firstSeen.Clear();
                selRig = null;
                roomJoinTime = Time.time;
            }
            foreach (var rig in AllRigs())
            {
                var np = rig?.OwningNetPlayer;
                if (np == null) continue;
                if (!firstSeen.ContainsKey(np.UserId)) firstSeen[np.UserId] = Time.time;
            }
        }

        string JoinedText(VRRig rig)
        {
            var np = rig.OwningNetPlayer;
            if (np == null || !firstSeen.TryGetValue(np.UserId, out float t)) return "?";
            if (t - roomJoinTime < 3f) return T("was here before me", "byl przede mna");
            int s = (int)(Time.time - t);
            string ago = s >= 60 ? $"{s / 60}m {s % 60}s" : $"{s}s";
            return T($"{ago} ago", $"{ago} temu");
        }

        // ---------- Mute / Report ----------
        // NOTE: these use the game's scoreboard internals; names can differ between versions.
        static GorillaPlayerScoreboardLine FindLine(NetPlayer np)
        {
            if (np == null) return null;
            return GorillaScoreboardTotalUpdater.allScoreboardLines
                .FirstOrDefault(l => l.linePlayer != null && l.linePlayer.UserId == np.UserId);
        }

        void DoMute()
        {
            if (selDummy && dummy != null)
            {
                dummyMuted = !dummyMuted;
                Status(dummyMuted ? T("Test rig muted", "Test rig wyciszony") : T("Test rig unmuted", "Test rig odciszony"));
                return;
            }
            if (selRig == null || selRig.OwningNetPlayer == null)
            {
                Status(T("Select a player first", "Najpierw wybierz gracza"));
                return;
            }
            var line = FindLine(selRig.OwningNetPlayer);
            if (line == null) { Status(T("Player line not found", "Nie znaleziono gracza")); return; }

            bool newState = !line.muteButton.isOn;
            line.PressButton(newState, GorillaPlayerLineButton.ButtonType.Mute);
            Status((newState ? T("Muted: ", "Wyciszono: ") : T("Unmuted: ", "Odciszono: ")) + selRig.OwningNetPlayer.NickName);
        }

        void DoReport()
        {
            if (selDummy && dummy != null)
            {
                Status(T("TEST: report not sent", "TEST: zgloszenie nie wyslane"));
                return;
            }
            if (selRig == null || selRig.OwningNetPlayer == null)
            {
                Status(T("Select a player first", "Najpierw wybierz gracza"));
                return;
            }
            var np = selRig.OwningNetPlayer;
            GorillaPlayerScoreboardLine.ReportPlayer(np.UserId, ReasonTypes[reasonIndex], np.NickName);
            Status(T("Reported: ", "Zgloszono: ") + np.NickName);
        }

        // ---------- Lobby hop ----------
        IEnumerator HopRoutine()
        {
            hopping = true;
            var trigger = PhotonNetworkController.Instance.currentJoinTrigger; // same map
            if (trigger == null)
            {
                Status(T("Join a map first", "Najpierw wejdz na mape"));
                hopping = false;
                yield break;
            }

            Status(T("Hopping...", "Zmieniam lobby..."));
            PhotonNetwork.Disconnect();

            float t = 0;
            while ((PhotonNetwork.InRoom || !PhotonNetwork.IsConnectedAndReady) && t < 15f)
            {
                t += Time.deltaTime;
                yield return null;
            }
            yield return new WaitForSeconds(1f);

            PhotonNetworkController.Instance.AttemptToJoinPublicRoom(trigger);
            hopping = false;
        }
    }
}
