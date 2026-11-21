import {resourceIcons} from './resource-icons.generated';
// UI-only mapping. Resource kind, version evidence and C4 shapes remain unchanged.
const kinds:Readonly<Record<string,string>>={
 Agent:'robot',AgentRevision:'robot',AgentDeployment:'server',Tool:'tool',ToolDefinition:'tool',ToolSet:'package',ToolProvider:'plug-connected',
 KnowledgeSource:'book',DataSource:'database',DataSourceQuery:'search',DataSourceProfile:'database',Storage:'database',
 ConnectedService:'plug-connected',ConnectedServiceOperation:'plug-connected',ModelProfile:'cpu',ModelProvider:'cpu',RuntimeProfile:'server',
 ExtensionRegistration:'puzzle',Flow:'route',Trigger:'clock',TransformationFunction:'puzzle',Schema:'schema',Artifact:'file-text',Source:'file-text',System:'building',Secret:'key'
};
export function resourceIcon(kind:string):string{return resourceIcons[Object.hasOwn(kinds,kind)?kinds[kind]!:'cube']!;}
