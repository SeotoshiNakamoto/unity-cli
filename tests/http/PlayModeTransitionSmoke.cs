// Temporary PlayMode test; copy to the existing PlayMode test assembly only.
using NUnit.Framework;
using UnityEngine;

namespace UnityCliConnector.HttpTests
{
    public class PlayModeTransitionSmoke
    {
        [Test]
        public void PureTransition()
        {
            Assert.That(Application.isPlaying, Is.True);
            Assert.That(2 + 2, Is.EqualTo(4));
        }
    }
}
