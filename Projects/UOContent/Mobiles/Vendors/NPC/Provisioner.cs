using System.Collections.Generic;

namespace Server.Mobiles
{
    public class Provisioner : BaseVendor
    {
        private readonly List<SBInfo> m_SBInfos = new();

        [Constructible]
        public Provisioner() : base("the provisioner")
        {
            SetSkill(SkillName.Camping, 45.0, 68.0);
            SetSkill(SkillName.Tactics, 45.0, 68.0);
        }

        public Provisioner(Serial serial) : base(serial)
        {
        }

        protected override List<SBInfo> SBInfos => m_SBInfos;

        public override void InitSBInfo()
        {
            m_SBInfos.Add(new SBProvisioner());

            if (IsTokunoVendor)
            {
                m_SBInfos.Add(new SBSEHats());
            }
        }

        public override void Serialize(IGenericWriter writer)
        {
            base.Serialize(writer);

            writer.Write(0); // version
        }

        public override void Deserialize(IGenericReader reader)
        {
            base.Deserialize(reader);

            var version = reader.ReadInt();
        }
    }
}
