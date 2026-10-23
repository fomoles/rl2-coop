#if RL2_GAME
using RL2Coop.Net.Protocol;
using UnityEngine;

namespace RL2Coop.Plugin.RL2
{
    /// <summary>
    /// Another player's hero in our world: a copy of the local hero's visuals
    /// with every script, collider and rigidbody stripped, so it can be seen
    /// but cannot be hit, push anything or run game logic. Its animator is set
    /// to the state the owner's animator is in.
    /// </summary>
    internal sealed class Puppet : MonoBehaviour
    {
        internal byte Id;
        internal string Name;
        private Animator _animator;

        internal static Puppet Create(byte id, string name)
        {
            if (!PlayerManager.IsInstantiated) return null;
            var player = PlayerManager.GetPlayerController();
            var source = player != null && player.Animator != null ? player.Animator.gameObject : null;
            if (source == null) return null;

            var root = new GameObject($"RL2Coop Puppet #{id}");
            Object.DontDestroyOnLoad(root);
            // The hero's animator sits on the hero's root, so the copy is the
            // whole hero. Copied under an inactive parent its scripts never
            // wake, and they are gone before the parent is switched on.
            root.SetActive(false);
            var visual = Instantiate(source, root.transform);
            // The root stands where the owner's hero stands and faces right;
            // the visual keeps its offset from the hero, taken facing right.
            var offset = source.transform.position - player.transform.position;
            var scale = source.transform.lossyScale;
            if (!player.IsFacingRight) offset.x = -offset.x;
            visual.transform.localPosition = offset;
            visual.transform.localScale = new Vector3(Mathf.Abs(scale.x), scale.y, scale.z);
            Strip(visual);

            var p = root.AddComponent<Puppet>();
            p.Id = id;
            p.Name = name;
            p._animator = visual.GetComponent<Animator>();
            // Its animation events call into hero scripts that are not there.
            if (p._animator != null) p._animator.fireEvents = false;
            root.SetActive(true);
            return p;
        }

        private static void Strip(GameObject go)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
                t.gameObject.tag = "Untagged"; // the game finds its hero by tag
            // A component another one requires cannot go first; components
            // are usually added after what they require, so last-first, and
            // a few passes for the rest.
            for (var pass = 0; pass < 6; pass++)
            {
                var left = 0;
                var all = go.GetComponentsInChildren<Component>(true);
                for (var i = all.Length - 1; i >= 0; i--)
                {
                    var c = all[i];
                    if (c == null || !Strippable(c)) continue;
                    if (CanGo(c)) DestroyImmediate(c);
                    if (c != null) left++;
                }
                if (left == 0) return;
            }
        }

        private static bool Strippable(Component c) =>
            c is MonoBehaviour || c is Collider2D || c is Rigidbody2D || c is Collider || c is Rigidbody;

        /// <summary>False while another strippable component on the same object still requires this one's type.</summary>
        private static bool CanGo(Component c)
        {
            var type = c.GetType();
            foreach (var other in c.GetComponents<Component>())
            {
                if (other == null || other == c || !Strippable(other)) continue;
                foreach (RequireComponent r in other.GetType().GetCustomAttributes(typeof(RequireComponent), true))
                    if (Needs(r.m_Type0, type) || Needs(r.m_Type1, type) || Needs(r.m_Type2, type)) return false;
            }
            return true;
        }

        private static bool Needs(System.Type required, System.Type type) => required != null && required.IsAssignableFrom(type);

        internal void Apply(in PlayerStateMsg s)
        {
            transform.position = new Vector3(s.X, s.Y, transform.position.z);
            var right = (s.Flags & PlayerFlags.FacingRight) != 0;
            var scale = transform.localScale;
            if ((scale.x > 0) != right) transform.localScale = new Vector3(-scale.x, scale.y, scale.z);

            var dead = (s.Flags & PlayerFlags.Dead) != 0;
            if (_animator == null) return;
            _animator.gameObject.SetActive(!dead);
            if (s.AnimState == 0 || dead) return;
            var cur = _animator.GetCurrentAnimatorStateInfo(0);
            // Restart only on a change of state; within a state the animator
            // runs on its own and stays smooth between messages.
            if (cur.fullPathHash != s.AnimState) _animator.Play(s.AnimState, 0, s.AnimTime % 1f);
        }
    }
}
#endif
