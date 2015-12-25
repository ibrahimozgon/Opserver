﻿using System;
using System.Collections.Generic;

namespace StackExchange.Opserver.Data.SQL
{
    public partial class SQLNode
    {
        private Cache<AGClusterState> _agClusterInfo;

        public Cache<AGClusterState> AGClusterInfo
        {
            get
            {
                return _agClusterInfo ?? (_agClusterInfo = new Cache<AGClusterState>
                {
                    CacheForSeconds = Cluster.RefreshInterval,
                    UpdateCache = UpdateFromSql(nameof(AGClusterInfo), async conn =>
                    {
                        var sql = GetFetchSQL<AGClusterState>() + "\n" +
                                  GetFetchSQL<AGClusterMemberInfo>() + "\n" +
                                  GetFetchSQL<AGClusterNetworkInfo>();

                        AGClusterState state;
                        using (var multi = await conn.QueryMultipleAsync(sql))
                        {
                            state = await multi.ReadFirstOrDefaultAsync<AGClusterState>();
                            if (state != null)
                            {
                                state.Members = await multi.ReadAsync<AGClusterMemberInfo>().AsList();
                                state.Networks = await multi.ReadAsync<AGClusterNetworkInfo>().AsList();
                            }
                        }
                        if (state != null)
                        {
                            foreach (var m in state.Members)
                            {
                                m.IsLocal = string.Equals(m.MemberName, ServerProperties.Data?.ServerName, StringComparison.InvariantCultureIgnoreCase);
                            }
                        }
                        return state;
                    })
                });
            }
        }

        public class AGClusterState : ISQLVersioned
        {
            public Version MinVersion => SQLServerVersions.SQL2012.RTM;

            public string ClusterName { get; internal set; }
            public QuorumTypes QuorumType { get; internal set; }
            public QuorumStates QuorumState { get; internal set; }
            public int? Votes { get; internal set; }

            public List<AGClusterMemberInfo> Members { get; internal set; }
            public List<AGClusterNetworkInfo> Networks { get; internal set; }

            public string GetFetchSQL(Version v) => @"
Select cluster_name ClusterName,
       quorum_type QuorumType,
       quorum_state QuorumState
  From sys.dm_hadr_cluster;";
        }

        public class AGClusterMemberInfo : ISQLVersioned
        {
            public Version MinVersion => SQLServerVersions.SQL2012.RTM;
            public string MemberName { get; internal set; }
            public ClusterMemberTypes Type { get; internal set; }
            public ClusterMemberStates State { get; internal set; }
            public int? Votes { get; internal set; }
            public bool IsLocal { get; internal set; }
            
            public string GetFetchSQL(Version v) => @"
select member_name MemberName,
       member_type Type,
       member_state State,
       number_of_quorum_votes Votes
from sys.dm_hadr_cluster_members;";
        }

        public class AGClusterNetworkInfo : ISQLVersioned
        {
            public Version MinVersion => SQLServerVersions.SQL2012.RTM;
            public string MemberName { get; internal set; }
            public string NetworkSubnetIP { get; internal set; }
            public string NetworkSubnetIPMask { get; internal set; }
            public int? NetworkSubnetPrefixLength { get; internal set; }
            public bool IsPublic { get; internal set; }
            public bool IsIPV4 { get; internal set; }

            public string GetFetchSQL(Version v) => @"
select member_name MemberName,
       network_subnet_ip NetworkSubnetIP,
       network_subnet_ipv4_mask NetworkSubnetIPMask,
       network_subnet_prefix_length NetworkSubnetPrefixLength,
       is_public IsPublic,
       is_ipv4 IsIPV4
  from sys.dm_hadr_cluster_networks;";
        }
    }
}