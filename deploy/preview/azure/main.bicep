targetScope = 'resourceGroup'

param location string
param resourcePrefix string
param ownershipTags object
@allowed(['Standard_B2ms', 'Standard_B4ms'])
param vmSize string = 'Standard_B2ms'
@description('Exact Canonical Ubuntu 24.04 image version; never latest.')
param imageVersion string
param adminUsername string = 'preview'
param sshPublicKey string
@minValue(32)
@maxValue(256)
param dataDiskGiB int = 64

resource networkSecurityGroup 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
  name: '${resourcePrefix}-nsg'
  location: location
  tags: ownershipTags
  properties: {
    securityRules: [{
      name: 'deny-all-inbound'
      properties: {
        priority: 100
        direction: 'Inbound'
        access: 'Deny'
        protocol: '*'
        sourcePortRange: '*'
        destinationPortRange: '*'
        sourceAddressPrefix: '*'
        destinationAddressPrefix: '*'
      }
    }]
  }
}

resource virtualNetwork 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: '${resourcePrefix}-vnet'
  location: location
  tags: ownershipTags
  properties: {
    addressSpace: { addressPrefixes: ['10.42.0.0/24'] }
    subnets: [{
      name: 'preview'
      properties: {
        addressPrefix: '10.42.0.0/26'
        defaultOutboundAccess: false
        networkSecurityGroup: { id: networkSecurityGroup.id }
      }
    }]
  }
}

// Explicit outbound connectivity. The NSG denies every inbound port, including SSH.
resource publicIp 'Microsoft.Network/publicIPAddresses@2024-05-01' = {
  name: '${resourcePrefix}-pip'
  location: location
  tags: ownershipTags
  sku: { name: 'Standard' }
  properties: { publicIPAllocationMethod: 'Static' }
}

resource nic 'Microsoft.Network/networkInterfaces@2024-05-01' = {
  name: '${resourcePrefix}-nic'
  location: location
  tags: ownershipTags
  properties: {
    networkSecurityGroup: { id: networkSecurityGroup.id }
    ipConfigurations: [{
      name: 'preview'
      properties: {
        privateIPAllocationMethod: 'Dynamic'
        subnet: { id: virtualNetwork.properties.subnets[0].id }
        publicIPAddress: { id: publicIp.id }
      }
    }]
  }
}

resource dataDisk 'Microsoft.Compute/disks@2024-03-02' = {
  name: '${resourcePrefix}-data'
  location: location
  tags: ownershipTags
  sku: { name: 'StandardSSD_LRS' }
  properties: {
    creationData: { createOption: 'Empty' }
    diskSizeGB: dataDiskGiB
    publicNetworkAccess: 'Disabled'
    networkAccessPolicy: 'DenyAll'
  }
}

resource vm 'Microsoft.Compute/virtualMachines@2024-07-01' = {
  name: '${resourcePrefix}-vm'
  identity: { type: 'None' }
  location: location
  tags: ownershipTags
  properties: {
    hardwareProfile: { vmSize: vmSize }
    securityProfile: {
      securityType: 'TrustedLaunch'
      uefiSettings: { secureBootEnabled: true, vTpmEnabled: true }
    }
    storageProfile: {
      imageReference: {
        publisher: 'Canonical'
        offer: 'ubuntu-24_04-lts'
        sku: 'server'
        version: imageVersion
      }
      osDisk: {
        name: '${resourcePrefix}-os'
        createOption: 'FromImage'
        deleteOption: 'Delete'
        managedDisk: { storageAccountType: 'StandardSSD_LRS' }
      }
      dataDisks: [{
        lun: 0
        name: dataDisk.name
        createOption: 'Attach'
        deleteOption: 'Detach'
        managedDisk: { id: dataDisk.id }
      }]
    }
    osProfile: {
      computerName: resourcePrefix
      adminUsername: adminUsername
      customData: base64(loadTextContent('cloud-init.yml'))
      linuxConfiguration: {
        disablePasswordAuthentication: true
        provisionVMAgent: true
        ssh: { publicKeys: [{ path: '/home/${adminUsername}/.ssh/authorized_keys', keyData: sshPublicKey }] }
      }
    }
    networkProfile: {
      networkInterfaces: [{ id: nic.id, properties: { deleteOption: 'Detach' } }]
    }
    diagnosticsProfile: { bootDiagnostics: { enabled: true } }
    // No VM identity: application containers cannot obtain Azure credentials via IMDS.
  }
}

output virtualMachineId string = vm.id
output dataDiskId string = dataDisk.id
