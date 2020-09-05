﻿/*
 * CHANGE LOG - keep only last 5 threads
 * 
 * RESSOURCES
 */
using Gravity.Abstraction.Logging;

using Rhino.Api;
using Rhino.Api.Contracts.Attributes;
using Rhino.Api.Contracts.Configuration;
using Rhino.Api.Extensions;
using Rhino.Connectors.Xray.Framework;

using System;
using System.Collections.Generic;

namespace Rhino.Connectors.Xray
{
    /// <summary>
    /// XRay connector for running XRay tests as Rhino Automation Specs.
    /// </summary>
    [Connector(Connector.JiraXRay)]
    public class XrayConnector : RhinoConnector
    {
        #region *** Constructors ***
        /// <summary>
        /// Creates a new instance of this Rhino.Api.Components.RhinoConnector.
        /// </summary>
        /// <param name="configuration">Rhino.Api.Contracts.Configuration.RhinoConfiguration to use with this connector.</param>
        public XrayConnector(RhinoConfiguration configuration)
            : this(configuration, Utilities.Types)
        { }

        /// <summary>
        /// Creates a new instance of this Rhino.Api.Components.RhinoConnector.
        /// </summary>
        /// <param name="configuration">Rhino.Api.Contracts.Configuration.RhinoConfiguration to use with this connector.</param>
        /// <param name="types">A collection of <see cref="Type"/> to load for this repository.</param>
        public XrayConnector(RhinoConfiguration configuration, IEnumerable<Type> types)
            : this(configuration, types, Utilities.CreateDefaultLogger(configuration))
        { }

        /// <summary>
        /// Creates a new instance of this Rhino.Api.Components.RhinoConnector.
        /// </summary>
        /// <param name="configuration">Rhino.Api.Contracts.Configuration.RhinoConfiguration to use with this connector.</param>
        /// <param name="types">A collection of <see cref="Type"/> to load for this repository.</param>
        /// <param name="logger">Gravity.Abstraction.Logging.ILogger implementation for this connector.</param>
        public XrayConnector(RhinoConfiguration configuration, IEnumerable<Type> types, ILogger logger)
            : base(configuration, types, logger)
        {
            // setup provider manager
            ProviderManager = new XrayAutomationProvider(configuration, types, logger);

            // connect on constructing
            Connect();
        }
        #endregion
    }
}
