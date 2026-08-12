# Resources

Management resources use the Agentstration-native envelope `uid`, `apiVersion`, `kind`, `metadata { name, tags, annotations }`, and a typed `definition`. There is no resource group, location, provider namespace, ARM path, or `type/properties` pair.

Resources are not runtime instances. They are governed declarations from which runtime state can be reconstructed. See the [resource reference](../reference/resources/overview.md).
